# 流程运行数据与末节点回调治理任务清单

**对应计划**：`docs/2026-07-29-flow-runtime-data-and-callback-plan.md`
**状态**：最终实施指令已生效，按依赖连续推进
**执行约束**：核心改造遵循测试先失败、最小实现、测试转绿、阶段验收、阶段 Git 提交；真实外部依赖无法验证时不得伪造通过。
**任务格式**：每项均包含目标、文件、动作、禁止事项、测试、验收、依赖、风险和回滚。

**2026-07-29 执行结果**：已勾选项表示实现或门禁动作完成；T024 的完成含义是“按停止线结束并给出不通过裁决”，不是 5 万容量通过。未勾选的 T003、T011、T016、T017、T020、T022、T025 仍是上线阻塞或未证明项，详见最终验收报告。

## Phase 1：S0 基线与规划门禁

- [x] T001 [P] 固化真实部署拓扑和 Flowable 5 秒超时来源

  - **目标**：把 Flowable 数据库、HTTP Task、Job Executor 和持久化配置从推测变成可复核事实。
  - **需要检查或修改的文件**：检查 `appsettings.json`、`Infrastructure/Flowable/FlowableOptions.cs`、`Infrastructure/Flowable/FlowableHttpClient.cs`、`bpmn/门户资讯审批/portal_content_approval.bpmn`；把必要结论维护到本任务文件和最终 `docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`；检查实际服务器 Flowable 容器/服务配置但不修改。
  - **具体实施动作**：记录各环境 Flowable 版本、数据源类型、脱敏 JDBC URL、连接池、持久卷、HTTP Task 连接/读取超时、重试和 Job Executor；把仓库 30 秒配置与日志 5 秒现象逐项对照；标注证据命令和时间。
  - **禁止事项**：不得读取或修改 `ACT_*` 业务数据；不得为了通过测试直接延长超时；不得把测试嵌入式数据库结论套用到生产。
  - **测试方式**：使用门户 BPMN 发起一条 A 组和一条 B 组 5 秒回调，关联 Flowable、流程中心和业务回调日志。
  - **验收标准**：报告明确回答 Flowable 实际数据库、5 秒超时来源、重试次数和持久卷；每个结论有配置或日志证据。
  - **依赖任务**：无。
  - **风险**：环境权限不足、不同环境配置漂移。
  - **回滚方式**：只读检查和文档新增，无运行态回滚；删除未评审报告即可。

- [x] T002 [P] 固化 ES、Redis 和现有数据质量基线

  - **目标**：确认三个 ES 索引的真实字段、数据量、缺失率、动态字段和 Redis 当前 Key/连接行为。
  - **需要检查或修改的文件**：检查 `Infrastructure/ElasticSearch/ElasticSearchOptions.cs`、`Infrastructure/ElasticSearch/ElasticSearchServiceImpl.cs`、`Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`、`Infrastructure/DistributedLock/RedisDistributedLockService.cs`；把计数维护到最终验收文档，不新增独立报告。
  - **具体实施动作**：只读导出 Mapping/Settings/文档计数；抽样比对 businessId、processInstanceId、status、callback、推荐快照、语义版本和审计；统计 Flowable-only/ES-only 的可见差异；记录 Redis DB、Key 前缀、TTL、连接失败表现。
  - **禁止事项**：不得删除/重建索引；不得扫描或输出敏感 Header/Token；不得清空 Redis。
  - **测试方式**：对至少一条 running、completed、terminated、callback_failed 和一条压测流程做三方交叉检查。
  - **验收标准**：确认是否存在模型外动态字段、空推荐快照、孤儿、状态冲突和 Redis 启动硬依赖；报告可重复。
  - **依赖任务**：无。
  - **风险**：仅从 ES 无法发现所有 Flowable-only 孤儿。
  - **回滚方式**：只读检查，无运行态回滚。

- [ ] T003 [P] 建立端到端可观测性和故障分类基线

  - **目标**：在改造前能区分 Flowable、ES、DM8、Redis、业务回调和客户端取消造成的失败。
  - **需要检查或修改的文件**：`Api/Controllers/ProcessController.cs`、`Api/Controllers/TaskController.cs`、`Api/Controllers/CallbackController.cs`、`Application/Services/ProcessLifecycleAppService.cs`、`Application/Services/TaskExecutionAppService.cs`、`Application/Services/ProcessCallbackAppService.cs`、`Infrastructure/Flowable/*`、`Infrastructure/ElasticSearch/ElasticSearchServiceImpl.cs`、后续 DM8/Worker 文件。
  - **具体实施动作**：定义并实现结构化字段 `correlationId/businessId/processInstanceId/taskId/eventId/downstream/attempt/result/durationMs`；增加在途请求、下游耗时、连接池、队列、重试、死信和重复抑制指标；为 ES 无效响应建立独立错误指标。
  - **禁止事项**：不得记录 JWT、密码、完整回调 Header、敏感表单或无限长错误正文；不得把异常都归为 500。
  - **测试方式**：分别注入 Flowable 500、ES 无效响应、Redis 断开、业务 5 秒延迟和客户端断开，核对分类。
  - **验收标准**：单个业务 ID 可跨完整链路追踪；五类故障有不同指标和日志；无敏感信息泄露。
  - **依赖任务**：T001、T002 提供命名和环境基线。
  - **风险**：高基数字段导致指标系统膨胀。
  - **回滚方式**：通过配置关闭新增详细指标，保留基础错误计数。

- [x] T004 固化回归与压力测试契约

  - **目标**：在架构切换前建立可重复的启动、待办、完成、回调和故障注入测试。
  - **需要检查或修改的文件**：`performance/`、`Api/Controllers/TestController.cs`、`docs/test-reports/2026-07-29-portal-pressure-test-report.md`、测试项目；结果维护到最终验收文档，不新增阶段报告。
  - **具体实施动作**：固化 A/B 0/5/15/30/60 秒回调、重复回调、重复完成、ES/Redis 断开、客户端 60 秒超时、普通用户和超大待办用户场景；记录当前 API 状态码和错误码。
  - **禁止事项**：不得把共享测试环境直接打到 5 万；不得使用一个 JWT 的结果声称已验证 1 万真实身份；不得清理用户已有压测证据。
  - **测试方式**：先执行 10/100 小规模可重复基线，验证测试自身无数据污染。
  - **验收标准**：相同输入可重复得到基线报告；每个场景有准备、执行、清理和停止条件。
  - **依赖任务**：T001-T003。
  - **风险**：测试控制器使用进程内队列，重启后记录丢失。
  - **回滚方式**：测试脚本和文档独立删除；不影响生产路径。

## Phase 2：S1 可靠回调基础

- [x] T005 [US1] 确认 DM8 驱动并创建最小 Schema Migration

  - **目标**：建立流程中心最小业务一致性存储，不触碰 Flowable 数据库。
  - **需要检查或修改的文件**：`process.csproj`、`appsettings*.json`、`Program.cs`；新增 `Configuration/Dm8Options.cs`、`Infrastructure/Persistence/Dm8/`、`migrations/dm8/001_workflow_reliability.sql`、`migrations/dm8/001_workflow_reliability.rollback.sql`。
  - **具体实施动作**：实机验证 .NET 6 DM8 驱动、参数绑定、事务、CLOB、连接池、唯一冲突异常和原子租约语法；创建 `workflow_business_instance`、`workflow_callback_event` 首批表、唯一约束和索引；把密码改为环境变量/密钥注入。
  - **禁止事项**：不得创建或修改 Flowable `ACT_*` 表；不得把 ES 所有字段无差别建表；不得在应用启动时自动建生产表；不得提交明文密码。
  - **测试方式**：在隔离 Schema 执行正向/反向 Migration，验证重复 `business_id`、重复 `idempotency_key`、事务回滚和 CLOB。
  - **验收标准**：Migration 可重复执行且有版本记录；唯一键承担最终防重；连接失败不会被吞掉；回滚脚本不误删非本版本对象。
  - **依赖任务**：T001、T002；DM8 版本和驱动需用户/运维确认。
  - **风险**：DM8 SQL 方言、驱动异常码和跳锁能力与预期不同。
  - **回滚方式**：停止新写入后执行已验证回滚脚本；保留原 ES/Flowable 路径。

- [x] T006 [P] [US1] 定义业务绑定和回调事件领域状态机

  - **目标**：把回调幂等、重试、租约和死信规则编码为可测试的明确状态机。
  - **需要检查或修改的文件**：新增 `Domain/Workflow/WorkflowBusinessInstance.cs`、`Domain/Workflow/WorkflowCallbackEvent.cs`、`Domain/Workflow/WorkflowCallbackStatus.cs`、`Domain/Workflow/IWorkflowReliabilityStore.cs`、`Application/Dtos/WorkflowCallbackStatusDto.cs`。
  - **具体实施动作**：定义合法状态转换、幂等键规则、最大重试、退避、租约过期、死信、人工重投和投影版本；把 flow state 与 callback state 分开；明确重复事件返回已有 event。
  - **禁止事项**：不得用 Redis 状态作为最终判断；不得用时间戳单独生成不可复现幂等键；不得把 `completed` 同时表示 Flowable 结束和业务回调成功。
  - **测试方式**：状态机单元测试覆盖所有合法/非法转换、并发版本冲突和重复事件。
  - **验收标准**：非法跳转被拒绝；幂等键可由相同业务事件稳定重建；状态含义无二义性。
  - **依赖任务**：T005 的表结构决策。
  - **风险**：业务系统尚无幂等契约，节点事件标识不足。
  - **回滚方式**：领域类型尚未接入运行路径时可直接撤销；接入后按 Feature Flag 回退读取。

- [x] T007 [US1] 实现 DM8 事务存储和回调接收幂等提交

  - **目标**：Flowable 回调只等待一次轻量 DM8 事务，不等待最终业务系统。
  - **需要检查或修改的文件**：`Api/Controllers/CallbackController.cs`、`Application/Services/ProcessCallbackAppService.cs`、`Application/Dtos/FlowableCallbackDto.cs`、`Program.cs`；新增 `Infrastructure/Persistence/Dm8/WorkflowReliabilityStore.cs`、`Application/Services/WorkflowCallbackInboxService.cs`。
  - **具体实施动作**：校验请求和业务绑定；生成幂等键；单事务 insert-or-get callback event 并更新 callback state；提交成功后立即返回 `eventId/accepted/duplicate`；DM8 提交失败返回 503/5xx；旧同步业务调用置于 Feature Flag 后，仅用于紧急回退。
  - **禁止事项**：不得 `Task.Run`、内存 Channel 后先回 2xx、写 Redis 后回 2xx或捕获 DM8 异常返回成功；不得在事务内调用业务 HTTP、ES 或 Flowable。
  - **测试方式**：并发提交 100 次相同回调、DM8 断连、事务提交前崩溃、提交后响应前断开。
  - **验收标准**：相同幂等键只有一条事件；提交后响应丢失的重试返回 duplicate 2xx；DM8 不可用不丢事件且不伪成功；事务时长有指标。
  - **依赖任务**：T005、T006。
  - **风险**：回调目前匿名，伪造请求可写入 Inbox。
  - **回滚方式**：关闭 Inbox 接收 Feature Flag 回到旧路径；已持久事件保留不删除，防止重复。

- [x] T008 [P] [US1] 固化回调事件标识和下游幂等传递

  - **目标**：让相同 Flowable 回调稳定生成同一 `eventId/idempotencyKey`，并把该标识传给最终业务系统。
  - **需要检查或修改的文件**：`Application/Dtos/FlowableCallbackDto.cs`、`Application/Services/WorkflowCallbackInboxService.cs`、`Application/Services/BusinessCallbackDispatcher.cs`、`bpmn/门户资讯审批/portal_content_approval.bpmn`、其他包含 `frameworkCallbackUrl` 的 BPMN。
  - **具体实施动作**：按 processInstanceId、callback activity、callback type 生成稳定幂等键；Inbox 唯一约束最终拦截重放；业务 HTTP 使用 Header 或现有 Body 传递 eventId/idempotencyKey；保留现有内网访问边界。
  - **禁止事项**：不得新增 mTLS、PKI、复杂 HMAC 或渗透测试；不得用 Redis nonce 作为最终防重；不得用时间戳生成每次不同的幂等键。
  - **测试方式**：相同回调并发 100 次、响应丢失后重投、Worker 失败重投，验证标识稳定且只落一个事件。
  - **验收标准**：同一业务事件键稳定；DM8 唯一约束拦截重复；最终业务系统收到可复用幂等标识。
  - **依赖任务**：T006；可与 T007 的接收路径测试并行。
  - **风险**：部分历史 BPMN 未提供 callback activity ID，需要使用明确且可复现的兼容键。
  - **回滚方式**：保留 eventId 字段但停止向下游传递；不得删除已写事件或改变其幂等键。

- [x] T009 [US1] 实现有租约、有限并发的 Callback Worker

  - **目标**：可靠异步调用最终业务系统，支持重启恢复、退避、死信和下游隔离。
  - **需要检查或修改的文件**：`Program.cs`、`Application/Services/ProcessCallbackAppService.cs`；新增 `Application/Workers/WorkflowCallbackWorker.cs`、`Application/Services/BusinessCallbackDispatcher.cs`、`Configuration/WorkflowCallbackWorkerOptions.cs`。
  - **具体实施动作**：通过 DM8 原子租约领取 pending/retry；本地全局并发和每下游并发均有硬上限；使用 `IHttpClientFactory`、CancellationToken、响应正文限长；按状态码分类重试；成功/重试/死信更新 DM8；进程停止时停止领取并释放/等待租约过期。
  - **禁止事项**：不得无限并发、无限重试、同步 `.Result`、进程内唯一队列或以 Redis 计数替代 DB 租约；不得默认重试所有 4xx。
  - **测试方式**：A/B 0/5/15/30/60 秒、网络拒绝、429/500/400、Worker kill -9、租约过期、多实例竞争。
  - **验收标准**：同一事件最终只执行一次业务副作用或由业务幂等抑制；慢下游不拖垮其他下游；重启继续；超限进入死信；Redis 关闭仍可运行。
  - **依赖任务**：T007；下游幂等契约需确认。
  - **风险**：HTTP 成功后、DM8 标记成功前崩溃形成 at-least-once 重投。
  - **回滚方式**：暂停 Worker 领取，保留事件；修复后继续，无需删除或重建事件。

- [x] T010 [US1] 提供回调状态查询、死信和人工重投接口

  - **目标**：让延迟执行从“用户长等”变成可查询、可运维的状态。
  - **需要检查或修改的文件**：新增 `Api/Controllers/WorkflowCallbackAdminController.cs`、`Application/Services/WorkflowCallbackAdminService.cs`、相关 DTO；更新鉴权策略配置。
  - **具体实施动作**：提供按 eventId/businessId/processInstanceId 查询；只允许 dead_letter/明确状态人工重投；记录操作者、原因和时间；输出脱敏错误、次数和下次时间；限制分页大小。
  - **禁止事项**：不得允许未认证调用；不得编辑历史 payload 后无痕重投；不得通过删除旧事件绕过唯一键。
  - **测试方式**：权限、越权、非法状态重投、并发双重投、分页上限、敏感信息脱敏。
  - **验收标准**：人工操作可审计；并发重投只产生一次有效状态转换；接口不泄露 Header/Token。
  - **依赖任务**：T006、T009。
  - **风险**：人工重投仍可能触发下游重复，需要业务幂等。
  - **回滚方式**：关闭管理端点；Worker 和已持久事件不受影响。

- [ ] T011 [US1] 验收末节点异步切链

  - **目标**：证明用户完成和 Flowable HTTP ServiceTask 不再等待最终业务系统。
  - **需要检查或修改的文件**：`performance/`、`Api/Controllers/TestController.cs`、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`。
  - **具体实施动作**：执行 A/B 5/15/30/60 秒、100 次重复回调、Worker 重启、DM8 断开、Redis 断开、死信和人工重投；采集 Complete、Flowable 回调接受、业务最终成功三个独立延迟。
  - **禁止事项**：不得只看 HTTP 200；不得把“事件入库”写成“业务已成功”；不得在 S1 失败时继续 S2 切读。
  - **测试方式**：自动化集成测试 + Linux 测试服务器小规模压力，关联 eventId。
  - **验收标准**：Complete 延迟不随 B 组延迟线性增长；Flowable 回调只等待 DM8 提交；事件不丢、重复不重复执行业务、重启可恢复、死信可重投。
  - **依赖任务**：T007-T010。
  - **风险**：Flowable 引擎自身同步 HTTP 超时过短，DM8 提交也可能受影响。
  - **回滚方式**：停止升级；关闭新 Worker/Inbox Feature Flag，保留数据用于分析。

## Phase 3：S2 ES 职责收缩

- [x] T012 [US2] 扩展 DM8 定义配置和任务动作事实表

  - **目标**：承接 ES 中会影响执行正确性的节点规则和当前动作事实。
  - **需要检查或修改的文件**：新增 `migrations/dm8/002_definition_and_action.sql`、回滚脚本、`Domain/Workflow/WorkflowDefinitionConfig.cs`、`Domain/Workflow/WorkflowTaskAction.cs`、对应 Repository。
  - **具体实施动作**：创建 `workflow_definition_config` 和 `workflow_task_action`；定义 key+version、definitionId、content hash、prepared/applied/failed 状态、row_version 和投影版本；只保存当前已有动作字段。
  - **禁止事项**：不得建立第二套 Flowable 任务表；不得扩张为通用审计平台；不得把任意门户表单正文全部复制入库。
  - **测试方式**：版本唯一、动作状态转换、CLOB 快照、并发 row_version、Migration 回滚。
  - **验收标准**：在途实例可锁定定义版本；动作事实可区分 prepared 与 applied；表和索引有容量说明。
  - **依赖任务**：T005；S1 验收 T011 通过。
  - **风险**：存量 ES 语义文档缺少版本，无法自动还原所有历史版本。
  - **回滚方式**：停止新表写入并保留 ES 旧读；执行只删除本版本对象的回滚。

- [x] T013 [P] [US2] 迁移版本化定义配置并切换执行规则读取

  - **目标**：驳回、转派、Slot 和回调 URL 不再只依赖 ES 最新定义文档。
  - **需要检查或修改的文件**：`Application/Services/BpmnDeploymentAppService.cs`、`Application/Slots/IProcessSlotConfigProvider.cs`、`Infrastructure/Slots/ElasticSearchSlotConfigProvider.cs`、`Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`；新增 `Infrastructure/Slots/Dm8ProcessSlotConfigProvider.cs`。
  - **具体实施动作**：部署后取得 Flowable definition ID/version；DM8 写入版本化完整配置；ES 改为异步投影；运行实例绑定版本；Provider 以 key+version 读取 DM8，可经 Redis 缓存；存量无法确认版本的实例进入显式兼容/人工映射清单。
  - **禁止事项**：不得在 Flowable 部署成功后仅写 ES；不得继续按 processDefinitionKey 覆盖权威规则；不得悄悄把存量实例套用最新规则。
  - **测试方式**：同 Key 部署 V1/V2，两条实例分别验证驳回、转派、Slot 和回调 URL 使用各自版本；ES 停机测试。
  - **验收标准**：在途 V1 不受 V2 发布影响；ES 不可用时动作规则仍可读取；ES 可从 DM8 重建。
  - **依赖任务**：T012；T001 确认 Flowable definition version。
  - **风险**：部署 Flowable 成功、DM8 失败仍是跨系统不一致。
  - **回滚方式**：Feature Flag 回到 ES Provider；保留 DM8 配置和双读差异日志。

- [x] T014 [US2] 重构启动绑定、防重和孤儿对账

  - **目标**：让 DM8 唯一约束承担最终防重，并检测 Flowable/DM8/ES 跨系统半成功。
  - **需要检查或修改的文件**：`Application/Services/ProcessLifecycleAppService.cs`、`Infrastructure/Flowable/FlowableRuntimeServiceImpl.cs`、`Domain/Flowable/IFlowableRuntimeService.cs`、`Infrastructure/DistributedLock/RedisDistributedLockService.cs`、DM8 Store。
  - **具体实施动作**：DM8 先插入 `starting` 绑定；唯一冲突返回已有状态；调用 Flowable 后写回实例 ID/版本；增加按 businessKey 查询 Flowable 的对账；超时结果标为 reconcile_required，不盲目重启；推荐快照和回调配置写 DM8；Redis 锁仅作前置削峰。
  - **禁止事项**：不得仅凭 ES `running` 防重；不得在 Flowable 超时后直接再次启动；不得以 30 秒 Redis TTL 作为事务边界；不得删除无法对账的 Flowable 实例。
  - **测试方式**：并发 100 次同 businessId、Redis 断开、Flowable 成功后进程崩溃、响应丢失、DM8 写回失败、ES 全停。
  - **验收标准**：同一业务最多一个有效绑定；未知结果可自动/人工对账；ES 不可用不产生新的 ES orphan 路径；推荐快照不再只有 ES 副本。
  - **依赖任务**：T012、T013。
  - **风险**：Flowable businessKey 唯一性不是引擎强约束，仍需 DM8 预留和对账。
  - **回滚方式**：保留旧启动读路径 Feature Flag；不得删除新 DM8 绑定，避免回滚后重复启动。

- [x] T015 [US2] 把完成、驳回、转派、终止的定位和校验切到 DM8 + Flowable

  - **目标**：ES 故障不再阻断或错误授权核心动作。
  - **需要检查或修改的文件**：`Application/Services/TaskExecutionAppService.cs`、`Application/Services/ProcessLifecycleAppService.cs`、`Application/Services/ProcessQueryAppService.cs`、DM8 Store、Flowable Service 接口。
  - **具体实施动作**：DM8 定位业务绑定/定义版本；Flowable 校验任务存在、实例、assignee/candidate 和当前节点；动作先写 prepared，Flowable 结果后收敛；超时走历史对账；callback context 读取 DM8 task action；ES 只异步补充展示。
  - **禁止事项**：不得以 ES status、CanReject/CanReassign 或元数据存在性作为唯一执行依据；不得把 Flowable Complete 和 DM8 更新伪装成单库事务；不得在未知结果时自动重复 Complete。
  - **测试方式**：ES 停机下完成/转派/驳回/终止；并发相同任务；Complete 成功后响应丢失；prepared 后崩溃；越权用户。
  - **验收标准**：动作权限与 Flowable 实时状态一致；ES 只影响展示；prepared 记录最终可对账到 applied/failed；无幽灵成功。
  - **依赖任务**：T013、T014。
  - **风险**：历史 Flowable API 的查询成本和可用字段不足。
  - **回滚方式**：按动作独立 Feature Flag 回退；保留 DM8 动作记录并停止新写，不删除。

- [ ] T016 [US2] 建立版本化 ES 投影、重建和迁移校验

  - **目标**：把三个 ES 索引变成可延迟、可重建的查询投影。
  - **需要检查或修改的文件**：`Infrastructure/ElasticSearch/ElasticSearchServiceImpl.cs`、`Domain/ElasticSearch/IElasticSearchService.cs`、`Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`、新增 `Application/Workers/WorkflowProjectionWorker.cs`、`Infrastructure/ElasticSearch/ProjectionRebuilder.cs`、迁移工具目录。
  - **具体实施动作**：移除核心请求路径的 `Refresh.WaitFor` 依赖；投影携带 data_version 并拒绝旧版本覆盖；批量写入有界；无效响应抛出可识别错误；提供 businessId/定义版本/时间范围重建；迁移 ES-only 数据时生成校验报告。
  - **禁止事项**：不得删除原索引或原字段；不得把 ES 写失败传播成 Flowable 回滚；不得在无来源数据时声称索引可完整重建。
  - **测试方式**：ES 断开/恢复、乱序更新、版本冲突、重建中断续跑、存量样本哈希比对。
  - **验收标准**：ES 恢复后可追平；旧版本不能覆盖新版本；查询能区分暂不可用与空结果；三个索引都有来源说明。
  - **依赖任务**：T013-T015。
  - **风险**：旧审计只有 ES 来源，存量迁移前仍不可重建。
  - **回滚方式**：暂停 Projection Worker，切回旧同步投影 Feature Flag；保留原索引快照。

- [ ] T017 [US2] 验收 ES 降级和三方对账

  - **目标**：证明 ES 不再是核心动作的单点真相。
  - **需要检查或修改的文件**：`performance/`、对账脚本、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`。
  - **具体实施动作**：在 ES 全停、只读、慢响应、部分文档缺失下执行启动、完成、驳回、转派、终止、列表和回调；对账 Flowable、DM8、ES；验证重建。
  - **禁止事项**：不得把查询 200 空列表作为降级成功；不得在对账差异未清零时切换生产权威读。
  - **测试方式**：故障注入 + 小规模并发 + 存量抽样。
  - **验收标准**：核心动作正确；列表明确 503/降级；孤儿可发现修复；ES 重建后关键投影哈希一致。
  - **依赖任务**：T014-T016。
  - **风险**：对账查询本身给 Flowable 造成压力。
  - **回滚方式**：停止切流，恢复 ES 旧读；对账任务限速停用。

## Phase 4：S3 待办查询

- [x] T018 [US3] 扩展 Flowable 任务分页契约

  - **目标**：消除固定 `size=100` 和忽略 `total` 的截断。
  - **需要检查或修改的文件**：`Domain/Flowable/FlowableModels.cs`、`Domain/Flowable/IFlowableTaskService.cs`、`Infrastructure/Flowable/FlowableTaskServiceImpl.cs`、`Application/Dtos/PendingTaskDto.cs`。
  - **具体实施动作**：查询模型增加 `start/size/sort/order`；响应解析 `total/start/size`；定义稳定排序；限制 size；确认 candidateUser 是否包含已认领任务和两集合重复语义。
  - **禁止事项**：不得继续在 URL 固定 `size=100`；不得伪造精确 total；不得读取全部任务来计算第一页。
  - **测试方式**：构造 0、20、100、101、1000 条 assignee/candidate/重叠任务，验证页边界和排序。
  - **验收标准**：分页参数真实传到 Flowable；响应总数字段有契约测试；超过 100 条不再静默消失。
  - **依赖任务**：T004；可在 S2 后期开始，但切换依赖 T017。
  - **风险**：Flowable REST 对 involved/candidate 的排序和总数语义不满足需求。
  - **回滚方式**：保留旧 QueryTasks 方法，Feature Flag 回退；不得删除新测试数据前的证据。

- [x] T019 [US3] 重写待办双路归并和当前页数据补充

  - **目标**：第一页只处理与 pageSize 成比例的任务，ES/DM8 只补充当前页。
  - **需要检查或修改的文件**：`Application/Services/TaskExecutionAppService.cs`、`Application/Dtos/PendingTaskDto.cs`、`Infrastructure/Slots/Dm8ProcessSlotConfigProvider.cs`、ES/DM8 查询接口。
  - **具体实施动作**：校验 `PageIndex>=1`、`1<=PageSize<=100`；有界拉取 assignee/candidate；稳定归并、去重；先确定页 taskId，再批量查当前页绑定和投影；元数据缺失返回可诊断不一致，不静默 continue；定义精确 total 或游标契约。
  - **禁止事项**：不得在第一页预取两类各 100 条；不得为每个 task 单独查 ES/DM8；不得用长 TTL 缓存掩盖算法问题。
  - **测试方式**：普通用户、候选/办理重叠、同创建时间、深页、单用户 1 万待办、ES 部分缺失。
  - **验收标准**：ES MultiGet 数量不超过当前页不同实例数；页结果稳定无重复；pageSize 硬限制；错误不会伪装成无待办。
  - **依赖任务**：T018、T013-T017。
  - **风险**：双集合精确深页和 total 可能需要 Flowable 不提供的能力。
  - **回滚方式**：Feature Flag 回到旧待办实现；保留新接口兼容字段。

- [ ] T020 [US3] 执行待办性能门禁并裁决任务读模型

  - **目标**：以实测决定是否另立 DM8 任务读模型规格，本计划默认不建设。
  - **需要检查或修改的文件**：`performance/`、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`。
  - **具体实施动作**：测试普通用户和单用户 1 万待办；记录 P95/P99、错误率、Flowable 返回条数、ES MGet 数、DM8 查询数和内存；验证精确 total/深页需求。
  - **禁止事项**：不得在本任务中顺手创建任务读模型表；不得把缓存命中结果当源查询性能；不得只测试第一页小用户。
  - **测试方式**：100/1000/5000 查询阶梯，冷/热两组，ES/Redis 分别正常和降级。
  - **验收标准**：第一页不全量；P95/P99 达到最终指令门槛；若未达到，最终验收明确是 Flowable REST 能力、算法还是资源瓶颈，并将任务读模型列为未通过项，不伪造完成。
  - **依赖任务**：T019。
  - **风险**：用户尚未给出明确延迟 SLO。
  - **回滚方式**：只生成报告；不触发任务读模型建设。

## Phase 5：S4 Redis 保护层

- [x] T021 [US4] 解除 Redis 对应用启动和正确性的硬依赖

  - **目标**：Redis 停机时由 Flowable + DM8 保证正确性，Redis 只影响削峰和性能。
  - **需要检查或修改的文件**：`Program.cs`、`Infrastructure/DistributedLock/RedisDistributedLockService.cs`、`Infrastructure/DistributedLock/RedisOptions.cs`、`Application/Services/ProcessLifecycleAppService.cs`。
  - **具体实施动作**：改为可恢复/惰性连接；区分锁竞争与 Redis 故障；故障时绕过 advisory lock 并依赖 DM8 唯一键；增加连接、锁耗时和降级指标；所有 Key 加应用命名空间。
  - **禁止事项**：不得 Redis 故障时放弃 DM8 防重；不得把 lock timeout 延长为覆盖完整业务请求；不得吞掉重复启动的 DM8 唯一冲突。
  - **测试方式**：应用启动前 Redis 停机、运行中断开、锁过期、100 并发同 businessId。
  - **验收标准**：应用可启动；同业务仍只产生一个有效绑定；Redis 恢复后自动恢复保护；日志可区分降级和竞争。
  - **依赖任务**：T014、T017。
  - **风险**：StackExchange.Redis 默认连接行为配置不当导致长阻塞。
  - **回滚方式**：关闭 Redis 降级 Feature Flag，恢复原锁实现；DM8 唯一键保留。

- [ ] T022 [US4] 增加版本化节点配置缓存并验证降级

  - **目标**：降低重复读取 DM8 定义配置的成本，不改变规则真相。
  - **需要检查或修改的文件**：`Infrastructure/Slots/Dm8ProcessSlotConfigProvider.cs`、新增 `Infrastructure/Slots/RedisProcessSlotConfigCache.cs`、`Configuration/`、`Program.cs`。
  - **具体实施动作**：Key 包含 definitionKey+version+contentHash；TTL 和容量有上限；cache miss 单飞回源 DM8；部署新版本只写新 Key；Redis 错误直接回源；记录命中率和回源耗时。
  - **禁止事项**：不得只按 processDefinitionKey 缓存；不得在 Redis miss 时回 ES 作为权威；不得缓存动作结果或当前任务状态。
  - **测试方式**：V1/V2 并存、缓存穿透、Redis 断开、坏值、过期、并发 1000 次读取。
  - **验收标准**：版本不串；Redis 停机规则读取正确；坏缓存被丢弃并回源；有命中率证据。
  - **依赖任务**：T013、T021。
  - **风险**：大语义 JSON 增加 Redis 内存和网络成本。
  - **回滚方式**：关闭缓存 Provider 装饰器，直接读 DM8；无需迁移数据。

- [x] T023 [US4] 基于证据裁决限流和 1-5 秒热点查询缓存

  - **目标**：只为仍存在的热点增加保护，避免缓存用户特定/RBAC/动作结论。
  - **需要检查或修改的文件**：`performance/`、API 限流配置、潜在查询缓存实现文件、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`。
  - **具体实施动作**：基于 T020/S5 前测选择是否实施多实例限流、单飞和短缓存；定义 cache key、TTL、精确失效、权限隔离和 Redis 故障回源；无收益则明确不实现。
  - **禁止事项**：不得缓存认证失败、用户权限结果或长 TTL 我的待办；不得把 Redis 不可用转换为空列表；不得在没有指标前大范围加缓存。
  - **测试方式**：命中/未命中、权限隔离、claim/complete/reassign/reject 后失效、Redis 断开和击穿。
  - **验收标准**：实施项有前后对比且不影响正确性；不实施项有明确证据；Redis 停机可回源。
  - **依赖任务**：T020-T022。
  - **风险**：用户维度缓存键错误导致数据越权或陈旧待办。
  - **回滚方式**：配置关闭全部新缓存/限流，回源 Flowable+DM8+ES。

## Phase 6：S5 分级压力与上线门禁

- [x] T024 [US5] 执行 100→50000 分级混合压力测试

  - **目标**：验证改造后的容量、故障隔离、最终一致性和恢复能力。
  - **需要检查或修改的文件**：`performance/`、`Api/Controllers/TestController.cs`、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`。
  - **具体实施动作**：按 100、1000、5000、10000、50000 逐级；混合启动、待办、完成、A/B 5/15/30/60 秒、重复请求、Worker 重启和 ES/Redis/DM8 故障；每级对账 Flowable/DM8/ES；设置 CPU、内存、错误率、积压和恢复时间停止线。
  - **禁止事项**：不得跳级；不得在共享环境故障后继续放大；不得把请求发出数当成功并发数；不得用单 JWT 声称真实 1 万身份通过。
  - **测试方式**：Linux k6/等价工具 + 服务端资源采样 + 数据对账 + 事后恢复检查。
  - **验收标准**：每级有 P50/P95/P99、吞吐、错误率、回调最终成功率、死信、重复抑制、队列深度和恢复时间；只有前一级通过才进入下一级。
  - **依赖任务**：T011、T017、T020、T023。
  - **风险**：5 万活跃实例可能超出 Flowable 测试环境数据库/堆容量并污染共享环境。
  - **回滚方式**：自动停止负载、暂停 Worker 领取、保留事件和日志；按明确清理清单移除本轮测试数据。

- [ ] T025 完成切换、回滚演练和上线评审

  - **目标**：在生产切读前验证扩展式迁移、Feature Flag、数据对账和回滚。
  - **需要检查或修改的文件**：`docs/2026-07-29-flow-runtime-data-and-callback-plan.md`、`docs/2026-07-29-flow-runtime-data-and-callback-task.md`、`docs/2026-07-29-flow-runtime-data-and-callback-final-acceptance.md`；检查所有新增配置和 Migration。
  - **具体实施动作**：演练暂停/恢复 Worker、DM8→ES 重建、Redis 缓存关闭、旧/新读路径切换、死信处理、Flowable/DM8/ES 对账；列出责任人和告警阈值。
  - **禁止事项**：不得删除旧 ES 索引；不得在对账差异未清零时关闭旧读；不得把回滚定义为删除 DM8 事件。
  - **测试方式**：预生产完整切换和回滚演练，保留时间线、命令、结果和审批。
  - **验收标准**：回滚不产生重复流程或丢回调；Worker/投影可暂停续跑；运维能按 runbook 完成人工重投和对账。
  - **依赖任务**：T024 通过。
  - **风险**：跨系统切换窗口内新旧写入交叉。
  - **回滚方式**：按 runbook 逐项切回旧读，保留新写和事件，暂停异步消费者而非删除数据。

## 依赖顺序

```text
T001 + T002
      ↓
T003 → T004
      ↓
T005 → T006 → T007 → T009 → T011
              ↘ T008 ↗
                    ↘ T010
      ↓
T012 → T013 → T014 → T015 → T016 → T017
                                      ↓
                            T018 → T019 → T020
                                      ↓
                            T021 → T022 → T023
                                      ↓
                                    T024
                                      ↓
                                    T025
```

## 并行机会

- T001 与 T002 可并行，只做不同基础设施的只读核实；
- T003 的日志规范与 T004 的压测契约可在字段命名确认后并行；
- T006 领域模型与 T008 回调认证方案可并行；
- T013 定义配置迁移可与 T014 的对账接口设计并行，但切换必须按依赖顺序；
- T018 Flowable 分页契约可在 S2 后段独立开发，T017 通过前不得切流；
- T021 Redis 降级与 T020 待办性能报告可并行设计，实施以 DM8 防重已完成为前提。

## 独立验收增量

| 用户故事 | 独立目标 | 独立验收 |
| --- | --- | --- |
| US1 可靠回调 | Flowable 只等待 DM8 Inbox 提交 | 最终业务延迟 60 秒不拖长用户 Complete；事件不丢、可重试、可死信 |
| US2 ES 职责收缩 | 核心动作由 Flowable + DM8 保证 | ES 停机仍能正确启动防重和执行动作；ES 可重建 |
| US3 待办分页 | 第一页成本与 pageSize 成比例 | 只补充当前页；超大待办不截断；P95/P99 有证据 |
| US4 Redis 保护 | Redis 只影响性能 | Redis 停机应用可启动、流程正确、配置回源 |
| US5 容量验收 | 分级证明容量与恢复 | 每级指标、对账和停止门槛齐全，失败不跳级 |

## 建议 MVP

MVP 是 **T001-T011（S0 + S1）**：先查清真实部署并切断末节点同步业务回调。ES 职责迁移、待办分页和 Redis 新缓存均不应阻塞这个 MVP，也不能在 S1 未通过时提前上线。

## 格式与范围校验

- 任务编号 T001-T025 连续；
- 每项任务均包含目标、文件、动作、禁止事项、测试、验收、依赖、风险和回滚；
- 所有实现任务都指向当前真实文件或明确的新建路径；
- 未安排 ClickHouse、Flowable `ACT_*` 修改、Redis 真相存储、进程内 `Task.Run` 队列或本轮直接开发；
- 最终实施指令已经授权逐项实施；各阶段通过后自动进入下一阶段，不等待人工确认。
