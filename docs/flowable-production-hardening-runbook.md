# Flowable 生产化加固运行手册

## 组件职责

- Flowable + PostgreSQL：流程定义、运行实例、当前任务、变量、历史和异步 Job 的唯一真相。
- DM8：业务 ID 绑定、流程定义 ACTIVE 门禁、请求/任务动作幂等、推荐人快照、回调事件、租约、重试和死信。
- Elasticsearch：可丢失、可跳过、可重建的查询投影，不参与流程动作最终裁决。
- Redis：短期协调锁和跨实例下游并发槽，不保存唯一业务真相。

本方案不使用 ClickHouse，不增加消息中间件，不拆分独立 Worker。DM8 只使用
既有表的 CRUD；应用启动时不会建表、改表或执行迁移。

## 必需配置

生产环境必须通过环境变量或受控配置覆盖以下值：

```text
Dm8__Enabled=true
Dm8__ConnectionString=<普通 CRUD 账户连接串>
Dm8__ConnectionPoolSize=256
Dm8__ConnectionPoolTimeoutMilliseconds=60000
CallbackWorker__Enabled=true
CallbackWorker__WorkerCount=10
CallbackWorker__PollBatchSize=50
CallbackWorker__PerDownstreamConcurrency=5
CallbackWorker__LeaseSeconds=60
CallbackWorker__LeaseRenewIntervalSeconds=20
CallbackWorker__HttpTimeoutSeconds=30
Readiness__Enabled=true
Readiness__RequireElasticSearch=true
Readiness__ConsecutiveFailureThreshold=3
RuntimeTuning__MinWorkerThreads=200
RuntimeTuning__MinIoCompletionThreads=200
Redis__ConnectTimeoutMilliseconds=5000
Redis__SyncTimeoutMilliseconds=5000
Redis__AsyncTimeoutMilliseconds=5000
```

DM8 连接池参数属于客户端配置，不要求 DBA 或 DDL 权限。默认上限 128 用于本轮混合压力
基线；生产值必须结合数据库允许的会话数和应用实例数核算，所有实例连接池总和不能超过
数据库侧容量。

CLR 线程池下限必须在依赖客户端初始化前生效。Redis 的连接、同步和异步超时分别配置，
不要压缩为 1 秒；并发突发下的客户端调度排队会把健康 Redis 误判为依赖故障。

`OperationConcurrency` 先通过 `Total` 限制三类业务操作的总并发，再分别限制
流程启动、任务完成和待办查询。等待使用异步信号量，不占用工作线程；用于避免
应用冷启动时同时创建大量 DM8/Flowable 连接，不能用调大 DM8 连接池代替。
单个用户的大待办列表是热点查询，默认只允许 2 个并行查询，避免其拖慢流程启动。

`LeaseRenewIntervalSeconds` 必须小于 `LeaseSeconds`。下游并发只能在确认业务
系统支持幂等键并具备相应容量后调高。

## 上线与健康门禁

1. 启动进程后只使用 `/health/live` 判断进程存活。
2. 负载均衡只在 `/health/ready` 返回 200 后转发业务流量。
3. readiness 会实际读写 Redis，查询 DM8、Flowable 和 ES，并等待 Callback
   Worker 初始化。
4. readiness 未通过期间，除 `/health/*` 外的请求统一返回 HTTP 503。

服务已经 ready 后，单次周期探针超时只在健康详情中标记瞬时失败；连续达到
阈值才撤销 readiness，避免压力峰值中的一次 1 秒 Redis 探针超时触发 503
级联。

测试或本地开发可通过 `Readiness__Enabled=false` 关闭门禁；生产禁止关闭。

## 部署和启动幂等

部署接口新增 `deploymentRequestId` 和 `businessVersion`。相同请求或相同业务
版本重试不会创建新 Flowable 版本；同一业务版本内容哈希不同会拒绝覆盖。
只有最新 Flowable 定义存在匹配的 DM8 定义配置时才视为 `ACTIVE`。

```text
GET /api/flowable/bpmn/deployments/status
  ?processDefinitionKey=<key>&businessVersion=<version>
```

启动接口新增 `requestId`，调用方必须在网络重试时复用同一值。DM8 的
`businessId` 唯一约束和启动动作幂等记录是最终防线，Redis 锁仅减少竞争。
Flowable 调用超时时流程被标记为 `reconcile_required`；重试会先按
`businessKey=businessId` 回查，不能盲目再启动。

推荐人契约解析失败时启动会失败，不会以空推荐人快照继续创建流程。修正请求
内容后应使用新的 `requestId`。

## 回调、租约与故障处理

节点完成、驳回和流程结束的业务回调均先写入 DM8，然后由固定并发 Worker
发送。每次发送携带：

```text
Idempotency-Key: <稳定事件幂等键>
X-Callback-Event-Id: <DM8 事件 ID>
```

下游必须以 `Idempotency-Key` 去重。长回调期间 Worker 定时续租；旧 Worker
丢失租约后不得确认成功。HTTP 408、429 和 5xx 按配置退避重试，其他 4xx
直接进入死信。

```text
GET  /api/admin/callback-events/metrics
GET  /api/admin/callback-events?status=dead_letter
GET  /api/admin/callback-events/{eventId}
POST /api/admin/callback-events/{eventId}/retry
```

人工重试只允许死信事件，并记录操作幂等动作。告警至少覆盖 pending、
retry_waiting、dead_letter、最老 pending 年龄和租约丢失次数。

## 故障裁决

- Flowable/DM8/Redis 不可用：readiness 失败，拒绝新业务流量。
- ES 不可用：若 `RequireElasticSearch=true` 则 readiness 失败；若明确允许
  降级则设为 false，待办动作仍由 Flowable 实时状态和 DM8 绑定裁决。
- 单条 Flowable 任务暂未出现 DM8 绑定：待办页有界重试后跳过该条并告警，
  不让整页失败。
- Flowable 已部署但 DM8 同步失败：部署状态为 `reconcile_required`，启动
  门禁拒绝该最新版本。
- 回调下游变慢：用户完成请求不等待下游；观察积压并按下游容量调整并发。

## 压测

使用 `performance/portal-approval-load.js`。先烟测和热身，再运行 1 万档。
本轮不再执行 5 万档。1 万目标配置为 8000 仅启动、2000 完整流程、2000
待办查询；这表示最终启动 1 万个流程实例，不表示 1 万个同时 socket。

验收必须同时满足：

- checks 100%；
- HTTP 失败率 0%；
- 无重复流程实例；
- 无同一事件并发双消费；
- 无整页 `TASK_BINDING_INCONSISTENT`；
- A/B 最后节点完成耗时不随 B 组下游延时增加；
- 回调积压及排空过程可从管理指标观测。
