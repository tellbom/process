# Flowable 外挂 PostgreSQL 与 1 万/5 万极限压力测试报告

## 结论

Flowable 7.2 已在测试服务器上从容器内嵌存储切换为独立 PostgreSQL 12，
旧 Flowable 容器及测试数据按授权直接删除，未做备份。最终 1 万和 5 万
两档混合压力测试均达到 100% 检查成功。

外挂 PostgreSQL 本身不需要修改 Flowable 源码，但只换数据库不足以解决
原故障。测试同时确认并修正了两条应用链路：

1. 门户资讯审批末尾 HTTP ServiceTask 改为 Flowable 异步 Job，用户完成
   最后任务后不再同步等待业务回调；
2. 待办查询与流程启动之间存在 Flowable 任务先可见、DM8 绑定稍后提交的
   短暂窗口。待办查询改为有界重试，仍未可见的单条任务暂时跳过并告警，
   不再让整页返回 `400/TASK_BINDING_INCONSISTENT`。

最终结果不支持为当前在线故障引入 ClickHouse，也不支持用 Redis 长时间
缓存“我的待办”。Flowable 运行态应由 PostgreSQL 保存；DM8 保存业务绑定、
幂等和回调事件；Elasticsearch 是查询投影；Redis 只承担短期协调、限流和
显式可失效缓存。

## 环境与变更

- 测试服务器：16 CPU、62 GiB 内存，与其他测试服务共享宿主机；
- Flowable：`flowable/flowable-rest:7.2.0`，4 GiB Java 堆；
- PostgreSQL：`postgres:12`，独立 Docker 网络、命名卷，不暴露宿主机端口；
- PostgreSQL `max_connections=300`、`shared_buffers=2GB`；
- 流程中心：Linux self-contained 发布，systemd 管理，文件描述符上限
  262144；
- DM8：官方 .NET Provider，启用 `connPooling=true`，池上限 100；
- BPMN：`portal_content_approval`，最终部署版本 2；
- A 组业务回调立即返回，B 组业务回调延迟 15 秒；
- 压测使用一个真实 JWT 和同一个热点用户 `196045`，并非创建 1 万个真实
  Keycloak 身份；
- 5 万是最终创建 5 万个流程实例，实际峰值为 400 VU，不是同时建立 5 万个
  客户端 socket。

Flowable 容器通过标准数据源环境变量连接 PostgreSQL。PostgreSQL 初始化
日志和 Flowable 启动日志均确认使用 PostgreSQL 建表及连接，没有保留 H2
数据路径。

## 修正前基线

首次切换 PostgreSQL 后，在末尾 HTTP ServiceTask 仍同步、DM8 尚未连接池
化的情况下执行 1 万档：

- 10,000 次启动、2,000 次待办查询，共 12,000 个 k6 iteration；
- 检查成功率 15.21%，HTTP 失败率 84.78%；
- 启动成功 1,317/10,000，待办成功 461/2,000；
- 启动 p95 40.04 秒、p99 60 秒；
- Flowable 日志出现 HTTP 连接租约等待和 5 秒 SocketTimeout；
- 应用日志出现 DM8 网络连接失败；
- PostgreSQL 无错误，采样时仅约 22 个连接，证明 PostgreSQL 不是首个
  饱和点。

该基线证明“换 PostgreSQL”不能自动修复同步末回调、连接池和跨存储可见性
问题。

## 分级回归

### 异步语义烟测

- 26/26 检查成功；
- A/B 最后任务完成平均约 90/88 ms；
- B 组 15 秒业务回调在后台运行，用户响应不等待它；
- Flowable 异步 Job 和死信最终均为 0。

### 绑定竞态修正前的 1 千档

- 1,000 个流程启动和完整流程操作均成功；
- 200 次待办查询中 19 次返回 HTTP 400；
- 返回码为 `TASK_BINDING_INCONSISTENT`；
- 单独执行 100 次待办查询全部成功，证明它只在查询与启动重叠时发生。

### 新进程冷启动门禁

部署绑定修正后的全新进程立即施加 200 VU：

- 检查成功率 94.65%；
- 63 次启动和 20 次查询达到 60 秒客户端超时；
- 不再出现 `TASK_BINDING_INCONSISTENT`；
- Redis 服务无慢查询、无拒绝连接且 CPU 很低，但 StackExchange.Redis
  第一次建立连接时 150 条 `SET NX` 瞬时排队，触发 1 秒客户端超时。

这不是持续容量不足，而是上线后第一次业务波峰的冷启动惊群风险。生产发布
必须把 Redis、DM8、Flowable 的真实读写放入 readiness/warm-up 门禁，不能
只用不访问依赖的健康接口判断实例已就绪。

### 热态 1 千档

完全相同的 200 VU 配置热态复测：

- 1,600/1,600 检查成功；
- 1,000/1,000 流程启动成功；
- 200/200 待办查询成功；
- 启动 p95 632 ms，待办 p95 472 ms；
- A/B 最后任务完成 p95 509/513 ms。

## 正式压力结果

| 指标 | 1 万档 | 5 万档 |
|---|---:|---:|
| 仅启动流程 | 8,000 | 40,000 |
| 完整流程 | 2,000 | 10,000 |
| 启动总数 | 10,000 | 50,000 |
| 待办查询 | 2,000 | 10,000 |
| 最大 VU | 400 | 400 |
| 总检查数 | 16,000 | 80,000 |
| 检查成功率 | 100% | 100% |
| HTTP 失败率 | 0% | 0% |
| 总耗时 | 47.9 秒 | 3 分 27.6 秒 |
| 启动 p95 / p99 | 2.41 / 6.27 秒 | 1.62 / 2.99 秒 |
| 待办 p95 / p99 | 3.11 / 4.72 秒 | 1.17 / 2.62 秒 |
| 发起节点完成 p95 / p99 | 5.34 / 7.12 秒 | 1.71 / 2.68 秒 |
| 最后节点 A p95 / p99 | 1.35 / 1.63 秒 | 1.64 / 2.60 秒 |
| 最后节点 B p95 / p99 | 1.32 / 1.61 秒 | 1.64 / 2.64 秒 |

B 组 15 秒延迟没有体现在最后任务完成耗时中，证明用户请求已经与业务回调
解耦。A/B 最后任务耗时几乎一致。

## 5 万档结束后的状态

- Flowable 活跃任务 50,422；
- Flowable 异步 Job 0，死信 Job 0；
- PostgreSQL 数据库约 1,710 MB，日志中无 ERROR/FATAL/PANIC；
- Flowable 日志无 ERROR/OutOfMemory/死信异常；
- 应用日志无 RedisTimeout、未处理异常或绑定不一致；
- 压力结束后待办查询第一次 1.312 秒，随后两次约 137/136 ms；
- 应用、Flowable、PostgreSQL、Redis 均保持运行。

因此在本机 400 VU、5 万运行中任务模型下，没有观察到慢业务回调拖垮整个
流程中心。

## 后台回调积压

主请求通过不等于业务回调已及时完成。5 万档结束后，DM8 回调事件状态为：

- `pending=12,023`；
- `processing=3`；
- `succeeded=605`；
- `retry_waiting=0`；
- `dead_letter=1`。

唯一死信创建于 2026-07-29，原因是旧测试数据的非法 URL，不属于本轮。

当前单下游并发上限为 5，B 组一半事件每条固定占用 15 秒。仅慢事件的理论
最短排空时间约为 `6000 × 15 / 5 = 18,000 秒`，即约 5 小时。该隔离保护了
在线 API，却无法满足高峰后的回调新鲜度 SLA。

后续应依据真实业务后端容量：

1. 为回调队列增加积压量、最老事件年龄、成功率和死信告警；
2. 按下游域名/租户分区，避免一个慢下游阻塞其他下游；
3. 在下游明确支持幂等和更高并发后，再提高每下游并发或水平扩容独立
   Callback Worker；
4. 保留 DM8 持久化事件、租约、重试和死信，不能退回进程内 `Task.Run`。

## 数据职责裁决

### PostgreSQL

作为 Flowable 引擎事务库，保存运行任务、变量、历史和异步 Job。需要备份、
监控、连接池和容量治理。测试环境本轮按授权未备份。

### DM8

作为我方业务可靠性真相，保存 `businessId ↔ processInstanceId` 绑定、流程
定义版本、任务动作幂等、回调事件和状态。推荐人/业务绑定不能只依赖 ES 或
Redis。

### Elasticsearch

继续承担流程元数据、推荐快照和检索投影。ES 写失败或短暂查询失败仍可能让
页面数据暂时缺失，所以必须有 DM8/Flowable 对账，不能把 ES 当唯一事务
真相。

### Redis

保留为重复启动的快速建议锁、限流和可精确失效的短 TTL 缓存。Redis 异常时
由 DM8 唯一约束兜底。当前不应缓存用户长 TTL 待办，也不应把推荐人或当前
任务真相只放 Redis。新实例必须预热 Redis 连接。

### ClickHouse

当前不需要。它适合历史审计、趋势和离线分析，不解决在线事务一致性、末回调
隔离、待办查询或回调积压。

## 尚存风险

1. Flowable 数据库被清空后，重新部署同一 BPMN 时 Flowable 版本从 1 重新
   计数，而 DM8 仍保留旧版本唯一键；第一次部署会出现 Flowable 已成功、
   DM8 写入失败的跨库不一致。部署接口需要幂等补偿和对账。
2. 客户端 60 秒超时不等于服务端回滚。冷启动门禁中部分启动可能在客户端
   断开后继续成功，调用方必须以 `businessId` 幂等查询最终状态。
3. 有界重试后仍未发现 DM8 绑定的任务会被当前页暂时跳过并记录告警。必须
   用 Flowable/DM8/ES 三方对账发现持续孤儿，不能只看 ES 统计。
4. 本轮是单个热点用户和 400 VU，不等同于 1 万个真实用户、跨地域网络或
   5 万同时在线 socket 的容量认证。

## 证据

测试服务器保留以下原始文件：

- `/opt/process-flowable-pg/results/pg-10k-20260730143913.json`
- `/opt/process-flowable-pg/results/pg-binding-fix-1k-20260730151021.json`
- `/opt/process-flowable-pg/results/pg-binding-fix-warm-1k-20260730151405.json`
- `/opt/process-flowable-pg/results/pg-final-10k-20260730151451.json`
- `/opt/process-flowable-pg/results/pg-final-50k-20260730151708.json`
- 同名 `.log` 文件包含完整 k6 输出。

Flowable/PostgreSQL 编排保存在测试服务器
`/opt/process-flowable-pg/compose.yml`，密钥保存在权限为 600 的 `.env`，
未写入仓库。
