# 流程运行数据与末节点回调最终验收

**验收日期**：2026-07-29
**代码基线**：以本仓库当前真实代码为准
**测试范围**：门户资讯审批、DM8 独立 Schema、Flowable/ES/Redis 测试服务器、分级混合压力与故障注入
**最终裁决**：功能改造通过小规模真实验收；当前测试环境不具备 5 万并发容量，禁止据此批准生产上线或继续放大压力。

## 1. 数据库职责裁决

| 组件 | 最终职责 | 本轮结论 |
| --- | --- | --- |
| Flowable + 自管事务库 | 流程实例、当前任务、assignee/candidate、节点推进和结束状态的运行真相 | 保持权威；不直接访问或复制 `ACT_*` 表 |
| DM8 | `businessId → processInstanceId` 业务绑定、推荐快照、版本化执行配置、动作事实、Callback Inbox、幂等、租约、重试与死信 | 已建立独立 `FLOW_RELIABILITY` Schema 并接入核心路径 |
| Elasticsearch | 可延迟、可诊断的查询/审计投影 | 不再作为新流程启动防重、完成定位或末节点回调的唯一真相 |
| Redis | advisory 启动锁和可选性能保护 | Redis 断开时允许降级至 DM8 唯一约束；不保存流程真相 |
| ClickHouse | 仅可能用于未来大规模历史分析 | 当前没有在线链路需求，不引入 |

不实施“我的待办”长 TTL Redis 缓存。100 级测试已经证明主要等待发生在 Flowable REST/H2，而非 DM8 配置读取；缓存用户/RBAC 结果会增加陈旧待办和权限串用风险，不能解决运行数据库瓶颈。版本化定义配置的 Redis 缓存也暂缓，当前没有证据表明它是热点。

## 2. 现场基线

### 2.1 Flowable

- 容器镜像：`flowable/flowable-rest:7.2.0`；
- 数据源：镜像默认嵌入式 H2，JDBC 指向容器用户目录；
- 持久卷：`Mounts=[]`；
- JVM：`JAVA_OPTS=-Xmx512M`；
- Hikari 最大连接数：50；
- 容器无 restart policy；
- Flowable 7.2 JAR 中 HTTP 配置默认值为：
  - connect timeout 5000 ms；
  - connection request timeout 5000 ms；
  - socket timeout 5000 ms。

因此此前末节点约 5 秒失败来自 Flowable HTTP Task 默认超时，不是流程中心 `.NET HttpClient` 的 30 秒设置。

### 2.2 ES、Flowable 与 Redis

改造前只读统计：

- ES metadata：2267 条，其中 running 1053、completed 1210、terminated 4；
- ES audit：7297 条；
- ES semantic：5 条；
- Flowable 活跃实例/任务：1088；
- 精确集合比对发现 Flowable-only 活跃实例 35 条、ES-only running 0 条；
- ES 单节点集群为 yellow，未分配项来自副本；
- Redis 8.2.0，检查时 DB size 为 20，没有本轮新命名空间的历史真相 Key。

35 条 Flowable-only 记录直接证明原双写窗口已经产生孤儿。并发升高会增加 Flowable 成功后 ES 写入超时、连接池排队或进程中断落入该窗口的次数，因此“推荐人/待办偶发消失”的概率会随压力上升，而不是独立的 ES 与 Flowable 通讯协议随机丢包。

## 3. 已实施内容

### 3.1 DM8 Schema 与迁移

真实 DM8 独立 Schema `FLOW_RELIABILITY` 已执行并验证：

- `WORKFLOW_BUSINESS_INSTANCE`；
- `WORKFLOW_CALLBACK_EVENT`；
- `WORKFLOW_CALLBACK_LEASE`；
- `WORKFLOW_DEFINITION_CONFIG`；
- `WORKFLOW_TASK_ACTION`；
- `WORKFLOW_SCHEMA_VERSION`。

迁移文件位于 `migrations/dm8/001..004_*_up.sql`，并提供对应 down 脚本。DM8 脚本在现场应按语句执行；DIsql 文件模式对末尾语句的行为不稳定，runbook 不应把“脚本进程退出 0”单独当作迁移成功，必须再查版本表和对象。

### 3.2 末节点持久化异步

末节点链路现为：

```text
用户 Complete
→ Flowable HTTP ServiceTask
→ 流程中心校验 DM8 binding
→ DM8 同一事务插入幂等事件并把业务流程标为 completed/pending
→ 立即向 Flowable 返回
→ 有界 Worker 从 DM8 租约领取
→ 最终业务 HTTP
→ succeeded / retry / deadletter
```

未使用 `Task.Run`、内存 Channel 或 Redis 队列伪装可靠异步。下游收到稳定的 `X-Callback-Event-Id` 和 `Idempotency-Key`。管理接口支持脱敏分页查询和仅 deadletter 状态的可审计人工重投。

### 3.3 ES 职责收缩与存量迁移

- 新启动先在 DM8 预留 `starting`，DM8 唯一键承担最终防重；
- Flowable 启动后绑定实例和定义版本；
- 推荐快照及回调配置写入 DM8；
- 部署时把完整定义配置按 definition key + version + hash 不可变写入 DM8；
- 完成、驳回、转派、终止使用 DM8 定位并以 Flowable 实时状态校验；
- 动作以 prepared/applied/reconcile_required 记录；
- ES 查询失败不再伪装为空字典；
- 核心 ES 写移除 `Refresh.WaitFor`；
- 提供可重入的 ES metadata → DM8 最小绑定迁移接口。

现场迁移结果：ES source 2285 条，迁入 2267 条，已有 18 条，失败 0，冲突 0。迁移后原用户待办第一页恢复 HTTP 200，20 条结果，精确 total 902，单次 144 ms。

旧 ES 文档没有可信定义版本；这类存量实例明确走 legacy ES 配置兼容路径，不能悄悄套用最新 DM8 规则。新部署实例固定使用 DM8 版本配置。

### 3.4 待办查询

- Flowable 使用 `involvedUser + start + size + createTime desc` 真分页；
- 服务端限制 `pageSize <= 100`；
- 只对当前页 processInstanceId 批量补充 DM8/ES；
- 返回 `totalIsExact` 和 `hasMore`；
- 元数据不一致返回明确错误，不再 `continue` 静默丢任务。

## 4. 真实验收证据

### 4.1 15 秒 B 组与重复投递

服务器完整流程：

| 操作 | 时间 | 结果 |
| --- | ---: | --- |
| 启动 | 421 ms | 200 |
| 发起节点完成 | 229 ms | 200 |
| 末节点完成 | 196 ms | 200 |
| B 组最终业务回调 | 15001 ms | 后台完成 |

最终代码下 DM8 binding 同时为 `flowState=completed`、
`callbackState=succeeded`、定义版本 6；事件为 `succeeded`、
`attemptCount=1`、HTTP 200。重复投递 42 ms 返回，DM8 event total 仍为 1，
业务回调记录仍为 1。

### 4.2 小规模混合回归

迁移后 9 VU、24 iteration、32 个检查：

- 32/32 成功；
- HTTP 失败率 0；
- 待办平均 54.25 ms；
- 启动平均 190.48 ms；
- 首节点完成平均 144.18 ms；
- A 组末节点平均 172.5 ms；
- B 组末节点平均 172 ms。

### 4.3 100 级停止门禁

负载组成：100 次启动、100 次待办、20 条完整流程，最高 40 VU，B 组 5 秒。

- 260/260 检查成功，错误率 0；
- 启动 P95 7.18 s；
- 待办 P95 5.11 s；
- 首节点完成最高约 4.54 s；
- A 组末节点平均 294 ms；
- B 组末节点平均 306.3 ms；
- 回调完成后 pending/processing/retry/deadletter 均为 0；
- Flowable REST 实测出现 1.39 s、2.87 s、3.39 s、3.90 s、4.24 s 的响应；
- Flowable 容器采样约 850 MiB、388–404 PIDs，虽请求停止后 CPU 不高，但当前 H2/无卷形态已形成明显排队。

该级触发延迟停止线。按任务约束没有继续 1000、5000、10000、50000；这不是“未执行完”，而是分级门禁的正确失败结果。

### 4.4 故障注入

| 故障 | 结果 |
| --- | --- |
| Redis 运行中断开 | 启动仍为 200，耗时 3.857 s，DM8 唯一约束接管；Redis 已恢复 |
| ES 完全停止 | 新流程启动 922 ms、首节点完成 191 ms、待办 80 ms、末节点 168 ms，均为 200；最终回调 succeeded；ES 已恢复 |
| DM8 指向不可达端口 | 启动 500、339 ms；Flowable 同 businessKey 匹配数 0，没有“DM 失败但流程已启动”的伪成功 |

Redis 故障下首个请求仍会经历 StackExchange.Redis 断连检测，正确性通过但 3.857 秒性能需要在正式环境通过短超时、熔断或 sidecar 健康状态进一步收敛。

## 5. 未通过项与上线裁决

本轮不批准 5 万并发上线，原因：

1. Flowable 仍是嵌入式 H2、无持久卷、512 MiB 堆，100 级已经出现 5–7 秒 P95；
2. 尝试建立独立 PostgreSQL Flowable 验收实例时，测试宿主卷持续出现 `postmaster.pid`/`pg_wal` 写入权限错误；未修改现有 Flowable 容器，也没有用失败实例伪造外部数据库证据；
3. 35 条历史 Flowable-only 活跃孤儿仍需业务归属清单和人工对账，ES-only 迁移无法恢复 ES 从未保存的数据；
4. 尚未交付 DM8 → ES 的完整持久化投影 outbox/rebuilder，旧 ES 投影仍保留；
5. 未建立 1 万真实身份，只验证了一个真实鉴权身份和合成 assignee；
6. 未完成 60 秒慢回调、Worker kill -9、真实 deadletter 人工重投的服务器全链路组合门禁；
7. 当前测试鉴权模式和匿名 Flowable callback 的网络边界必须在正式部署复核。

生产前必须先：

- 把 Flowable 迁到受支持、可备份、有持久卷的外部事务数据库；
- 对该数据库做索引、连接池、锁等待、备份恢复和容量验收；
- 在相同部署拓扑重新从 100 开始逐级测试；
- 补齐 DM8 → ES 重建/投影闭环并清零对账差异；
- 处理 35 条 Flowable-only 清单；
- 完成 Worker 重启、死信重投和 60 秒下游故障门禁；
- 前一级全部通过后才允许进入下一级。

ClickHouse 和待办 Redis 缓存均不是上述阻塞项的解决方案。

## 6. 清理与环境恢复

- 本轮 124 个带 DM8 绑定的压力流程已通过项目终止接口收敛；
- 1 个无 DM8 绑定的 definition version 探针实例已直接删除；
- 本轮专用 businessKey 前缀在 Flowable 中残留 0；
- 临时 5013/5014 流程中心实例已停止；
- Flowable、ES、Redis 容器均恢复运行；
- 未删除旧 ES 索引、未清空 Redis、未修改 Flowable `ACT_*` 表。
