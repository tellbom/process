# 流程中心 V3 设计摘要报告

> **用途**：交付 Codex 作为其他业务系统集成流程中心的设计依据。
> **基线**：代码分支 `v3-node-desriptions`（HEAD `4b03ffd`），README 为 Patch Plan V1.3（Slot 三键分离）。
> **配套文档**：`README.md`（完整 API 测试规范，含全部请求/响应示例）；`specs/mian/process_center_patch_plan_v1.md`（补丁设计）；`docs/superpowers/specs/2026-08-31-v3-failed-delivery-design.md`（失败投递设计）。
> 本文是摘要，字段级细节以 README 为准；两者冲突时以 README「当前有效说明」章节为准。

---

## 1. 系统定位

流程中心 V3（代码名 FlowableWrapper，systemd 服务描述为 "Process Center V3 Pre-Outbox"）是对 **Flowable 7.2 工作流引擎的薄封装**，为业务系统提供统一的审批流接入层。

三层职责，不建第二套状态机：

| 层 | 职责 |
|---|---|
| 映射层 | businessType/businessId ↔ Flowable processDefinitionKey/processInstanceId；slotKey → Flowable 变量 |
| 包装层 | 统一 REST 契约、统一响应结构、统一错误码 |
| 审计层 | 每次审批动作写 ES 审计记录（选人快照、推荐范围越界标记） |

**核心原则**：Flowable 是唯一真相层——所有状态判定（实例状态、节点是否完成）必须查 Flowable，禁止本地推断。

V3 明确**不引入 Outbox**（这是 V4 的事）。ES 写失败只做一次同步重试 + Critical 日志 + 孤儿错误码，由人工介入。

## 2. 技术栈与部署形态

| 组件 | 选型 | 用途 |
|---|---|---|
| 服务端 | .NET 8（ASP.NET Core，Layered/Clean 分层） | REST API |
| 流程引擎 | Flowable 7.2，经 `flowable-rest` HTTP API 访问 | 流程执行唯一真相 |
| 元数据/审计 | Elasticsearch，3 个索引 | 实例元数据、审计、流程定义语义 |
| Redis | StackExchange.Redis | 分布式锁（启动幂等、转派锁定） |
| 认证 | JWT Bearer（Keycloak 或 TrustedJwt 模式） | 统一身份 |
| 消息通知 | 消息中心 HTTP + Keycloak client credentials | 待办/驳回提醒（失败不阻塞主流程） |

生产部署（`process-v3.service` + `v3-runtime.env`）：监听 `0.0.0.0:5013`，Flowable/ES/Redis 均为同机或近邻地址，systemd 托管，`Restart=always`。配置优先级：环境变量（`__` 双下划线分段）> appsettings.json。

## 3. 代码结构（分层）

```
process-v3/
├── Api/                  # Controllers（8 个）+ Filters（统一响应、全局异常）
├── Application/          # 应用服务
│   ├── Services/         # 生命周期/任务执行/部署/回调/查询/流程图渲染/失败投递/通知
│   ├── Slots/            # SlotVariableConverter、AssigneeContractConverter、slotConfig Provider
│   └── Dtos/             # 请求/响应契约
├── Domain/               # 抽象、异常、ES 文档模型、Flowable 接口（5 组 Service 接口）
├── Infrastructure/       # Flowable HTTP 实现、ES 实现、Redis 锁、CurrentUser、JWT
├── Configuration/        # IOptions 绑定（Jwt/Flowable/ES/Redis/映射/通知）
├── bpmn/                 # 已交付流程定义源文件（15 个业务流程）
└── docs/ specs/          # 设计规格与测试报告
```

依赖方向：Api → Application → Domain ← Infrastructure。Flowable 访问全部收敛在 `IFlowableRuntimeService / IFlowableTaskService / IFlowableHistoryService / IFlowableRepositoryService / IFlowableManagementService` 五组接口后面，经 `IHttpClientFactory` 管理。

## 4. 核心设计原则（集成方必须遵守的硬约束）

1. **Slot 三键分离（V1.3 核心）**：`slotKey`（前端提交 key）/ `roleKey`（推荐池来源 key）/ `variableName`（Flowable 变量名）三者职责固定，禁止互相推导、禁止同名兜底。slot 缺 `roleKey` 或 `variableName` 视为配置错误（`SLOT_ROLE_KEY_REQUIRED` / `SLOT_VARIABLE_NAME_REQUIRED`）。
2. **`NextSlotSelections` 是唯一最终生效人员来源**。`assigneeContract` 提供的推荐人只写 `RecommendedAssigneesSnapshot`（展示用），不影响执行路径。
3. **未知 `slotKey` 直接报错**（`SLOT_KEY_INVALID`），不静默忽略。
4. **`restrictToRecommended` 后端不强拦截**：提交推荐范围外人员流程照常推进，仅审计记录 `hasOutOfRecommendedRange=true` 并打 Warning 日志。
5. **`businessId` / `requestId` 语义**：`businessId` 是业务系统与流程中心交互的业务唯一凭据（API 路径主键）；`requestId` 标识"一次发起意图"，仅用于 `/start`，网络重试必须复用，新一轮审批必须换新值。同一 businessId 存在 running 实例时禁止再发起（`BUSINESS_PROCESS_ALREADY_RUNNING`）；上一轮 completed/terminated 后用新 requestId 开新轮次（`ApprovalRound` 递增，历史按 processInstanceId 隔离）。幂等由 Redis 分布式锁 + ES 查重实现。
6. **完成/驳回/转派不传 requestId**，用 `businessId` 定位最新轮次；并行节点或同一用户多待办时必须传 `/api/tasks/pending` 返回的 `taskId` 精确定位。
7. **流程中心不解析 BPMN 网关预测后续路径**。需要提前选多个下游处理人时，在当前节点 `slots` 显式声明多个槽；网关走向由完成时的 `businessVariables` 驱动。

## 5. 核心领域模型与数据存储

### 5.1 标识与生命周期

- `processInstanceId`：Flowable 实例 ID，= ES 元数据文档 ID。
- `status` 合法值：`running / completed / terminated / callback_failed`。
- 按 `businessId` 查询（进度/流程图/历史）始终返回**最新轮次**。

### 5.2 slotConfig（随 BPMN 部署，存 ES 语义索引）

节点级字段：`taskDefinitionKey`（必须等于 BPMN userTask id）、`nodeSemantic`（前端路由表单语义）、`pageCode`、`roleKey`（谁处理当前节点）、`assigneeMode`（single/multiple）、`canReassign`（默认 false，仅显式 true 开放）、`canReject`、`rejectOptions[]`、`isRejectTarget` / `rejectCode`（作为驳回落点）、`isStarterNode`、`isConvergencePoint`、`callbackUrl`、`slots[]`。

slot 级字段：`slotKey`、`roleKey`（推荐池来源，即 `RecommendedAssigneesSnapshot[slot.roleKey]`）、`label`、`mode`、`variableName`、`required`、`conditionalOn`（如 `IS_SOLVED==false`，条件不满足则跳过该槽：不校验 required、不写变量）、`restrictToRecommended`。

> 注意同名不同义：节点级 `roleKey` = 当前节点谁处理；slot 级 `roleKey` = 该选人槽从哪个推荐池取人。配置时通常 slot.roleKey 与下一节点外层 roleKey 一致，但靠显式声明而非命名推导。

### 5.3 assigneeContract（启动时传入）

`roles[]`（roleKey → 推荐用户列表）展开为 ES 中的 `RecommendedAssigneesSnapshot`；`nodeDescriptions[]`（roleKey → 节点说明）展开为 `NodeDescriptionsSnapshot`，供待办展示。

### 5.4 ES 三索引

| 索引 | 文档 ID | 内容 |
|---|---|---|
| `flowable-process-metadata` | processInstanceId | 实例元数据：状态、轮次、回调配置、推荐人快照、节点说明快照、NodeSemanticMap 副本 |
| `flowable-process-definition-semantic` | processDefinitionKey | 部署期节点语义配置，所有同 Key 实例共用；`nodeSemanticMap` 用 `dynamic:false` 防止 mapping fields 膨胀 |
| `flowable-audit-records` | — | 每次 CompleteTask 一条：action、operator、comment/rejectReason、选人快照、推荐人快照、越界标记 |

Flowable 自身数据库（ACT_* 表）是执行状态与历史任务的唯一真相，ES 只做元数据/审计镜像。

## 6. API 契约总览

**统一约定**：业务接口 HTTP 一律 200，`{ success, message, data }` / `{ success:false, message, errorCode }` 区分成败；认证统一 `Authorization: Bearer {jwt}`（按 `Jwt:UseridClaim`，默认 claim `userid`，fallback `sub/employee_id/uid`）。README 早期章节的 `X-User-Id` 仅为历史说明，**不是当前认证入口**。例外：`/api/admin/failed-deliveries*` 使用真实 HTTP 状态码（见 §9）。

| 能力 | 方法 + 路径 | 要点 |
|---|---|---|
| 部署流程 | POST `/api/flowable/bpmn/deploy` | multipart：`.bpmn` 文件 + `slotConfigJson` |
| 查节点配置 | GET `/api/flowable/bpmn/{key}/nodes` | 验证部署结果 |
| 启动流程 | POST `/api/processes/start` | requestId 必填；initialSlotSelections + assigneeContract + businessVariables + callback |
| 终止流程 | POST `/api/processes/terminate` | businessId + reason |
| 查待办 | GET `/api/tasks/pending` | employeeId（或 JWT）、businessType 多值 OR、分页 |
| 完成任务 | POST `/api/tasks/complete` | `action: 1` 通过 / `2` 驳回；nextSlotSelections 按 slotKey |
| 转派 | POST `/api/tasks/reassign` | 受当前节点 `canReassign` 控制；只作用于当前 Task |
| 流程进度 | GET `/api/processes/{businessId}/progress` | currentNodes + auditHistory + 推荐人矩阵 |
| 流程图渲染 | GET `/api/processes/{businessId}/flow-render` | bpmnXml + nodes/edges + activeTaskRenders + completedRecords |
| 审批历史 | GET `/api/processes/{businessId}/audit-history` | — |
| 流程状态 | GET `/api/processes/{businessId}/status` | 轻量 |
| 流程列表 | GET `/api/processes` | businessType/status 过滤 + 分页 |
| Flowable 回调入口 | POST `/api/callback/flowable` | 由 BPMN 末尾 ServiceTask 调用，非业务系统直调 |
| 失败投递管理 | GET/POST `/api/admin/failed-deliveries*` | 见 §9 |

**待办响应关键字段**（前端渲染契约）：`requiredSlots[]`（含 slotKey/roleKey/variableName/label/mode/required/restrictToRecommended）、`slotRecommendedUsers`（按 slotKey）、`restrictToRecommended`（按 slotKey）、`canReject`/`rejectOptions`/`canReassign`、`pageCode`/`pageUrl`（http(s) 时自动拼 businessId/taskId 等参数）、`businessTitle`/`nodeDescription`/`isOverdue` 等业务展示字段。

**候选人读取规则（固定）**：遍历 `requiredSlots[]`，用 `slotRecommendedUsers[slot.slotKey] ?? []` 初始化选人区；最终以用户提交的 `nextSlotSelections` 为准。

**主要错误码**：`FLOWABLE_START_FAILED`、`PROCESS_METADATA_INDEX_ORPHAN`（ES 两次写入均失败→孤儿实例）、`REQUEST_ID_REQUIRED`、`BUSINESS_PROCESS_ALREADY_RUNNING`、`SLOT_KEY_INVALID`、`SLOT_ROLE_KEY_REQUIRED`、`SLOT_VARIABLE_NAME_REQUIRED`、`REJECT_*` 系列（CODE/REASON_REQUIRED、NOT_ALLOWED、CODE_INVALID、TARGET_NOT_FOUND）、`REASSIGN_NOT_ALLOWED`。

## 7. 关键运行时链路

```
业务系统                          流程中心                        Flowable / ES / 消息中心
   │ POST /start(requestId) ──►  Redis锁+幂等查重 ──► ES元数据写入(1次重试)
   │                             变量转换(slotKey→variableName) ──► 启动实例
   │ ◄── firstTaskId/firstNodeSemantic/firstPageCode
   │
用户处理人 ◄── GET /tasks/pending（requiredSlots + 推荐人）── ES语义索引 + Flowable任务
   │ POST /tasks/complete(action=1, nextSlotSelections)
   │                    ──►     SlotVariableConverter(conditionalOn过滤/required校验/越界审计)
   │                            写审计(ES) ──► Flowable Complete ──► 节点回调业务系统(NODE_COMPLETED)
   │                            消息中心待办提醒(失败不阻塞)
   │ ... 逐节点推进 ...
   │ 末尾UserTask完成 ──► BPMN末尾 async HTTP ServiceTask ──► POST /api/callback/flowable
   │                    ──►     ES状态收口 completed ──► 业务系统流程完成通知
```

- **驳回**（action=2）：`rejectCode`+`rejectReason` 必填；校验 canReject、rejectOptions 归属、目标节点存在；Flowable 跳回目标节点；发 `REJECT_OCCURRED` 回调（含 `rejectTargetNodeKey`）+ 驳回提醒；flow-render 的 completedRecords 出现 `outcome=rejected_return`。
- **转派**：`newAssignees[]` + `reason`；当前 Task 的 assignee 变更，不动其他节点推荐人；completedRecords 出现 `outcome=reassigned`。
- **终止**：管理员语义；实例状态置 `terminated`。

## 8. 回调体系设计（业务系统接入重点）

三类回调，全部 POST JSON，由流程中心主动发出（前两类）：

| callbackType | 触发时机 | 载体 |
|---|---|---|
| `NODE_COMPLETED` | 用户任务完成（CompleteAsync 成功后） | 流程中心 HTTP 直调，不走 BPMN ServiceTask |
| `REJECT_OCCURRED` | 驳回发生 | 同上 |
| `PROCESS_COMPLETED` | 流程结束 | BPMN 末尾 Flowable async HTTP ServiceTask → `/api/callback/flowable` → 流程中心转发业务系统 |

**回调 Payload 公共字段**：`businessId`、`processInstanceId`、`processDefinitionKey`、`businessType`、`callbackType`、`taskDefinitionKey`、`nodeSemantic`、`rejectTargetNodeKey`（驳回时有值）、`lastAuditRecord`（action/operatorId/comment/rejectReason/operatedAt/slotSelections 快照）、`triggeredAt`。

**节点 callbackUrl 解析规则（固定四级）**：
1. slotConfig 节点 `callbackUrl` 为有效 URL → 发节点级回调；
2. 显式声明为 null/空 → 跳过且**不降级**；
3. 未声明 → 降级到启动时 `callback.url`（流程级兼容）；
4. 都没有 → 跳过。

**失败语义**：节点回调非 2xx 只记 Error 不阻塞已完成的任务（V3 无重试/幂等键，业务系统需自行容忍重复，这是已知边界）；流程结束回调失败走 Flowable Async Job 重试 → DeadLetter（见 §9）。

## 9. 失败投递管理（Failed Delivery）

V3 用 Flowable 原生链路承载末尾回调的可靠性，流程中心只提供中性契约门面（前端不得依赖 Flowable Job DTO）：

```
末尾UserTask完成 → async HTTP ServiceTask → Flowable Retry(R5/PT10M) → DeadLetter → Failed Delivery API
                                                                                    ├─ retry_delivery
                                                                                    └─ terminate_process
```

- **BPMN 强制约定**：末尾 ServiceTask `id` 以 `_framework_callback` 结尾、`flowable:async="true"`、`failedJobRetryTimeCycle R5/PT10M`、`failStatusCodes 4XX,5XX`、`ignoreException=false`；**不得**配置 `handleStatusCodes+boundaryEvent` 或 `ignoreException=true`（会吞掉错误，破坏重试链路）。只有 `_framework_callback` 结尾的 DeadLetter Job 进入该列表。
- **查询/详情**：`GET /api/admin/failed-deliveries`（过滤 businessId/processInstanceId/status 等，分页）与 `GET .../{deliveryId}`（详情含 stacktrace）。
- **retry**：`POST .../{deliveryId}/retry` → 把 DeadLetter Job 移回 executable，返回 202；此后成败由 Async Executor 决定（可能再次进 DeadLetter）。
- **terminate**：`POST .../{deliveryId}/terminate-process`，`reason` 必填 → 终止整个 active 实例（不留孤儿流程），ES 状态置 `terminated`。
- **DTO 语义**：`deliveryId` 当前等于 DeadLetter Job ID 但前端不得依赖此等式；`attemptCount`/`lastHttpStatus`/`lastFailedAt` 无法准确还原时返回 null，不用别的值冒充；`availableActions` 由后端计算，前端只按它渲染按钮。
- **HTTP 语义**：此组接口用真实状态码（400 INVALID_*/REASON_REQUIRED、401 未认证、404 FAILED_DELIVERY_NOT_FOUND、409 FAILED_DELIVERY_STATE_CHANGED/PROCESS_NOT_ACTIVE、502 FLOWABLE_ERROR）；管理动作记录结构化操作日志。
- **权限**：无硬编码角色，界面/管理权限由外部 RBAC 控制，后端只要求已通过统一认证。

## 10. 认证与安全

- 全局 `RequireAuthenticatedUser`（Default + Fallback Policy），未认证 401。
- JWT 两种模式（`Jwt:Mode`）：`Oidc`（Authority 校验）与 `TrustedJwt`（内网可信网关直传，当前生产采用），userid 取自 `Jwt:UseridClaim`。
- CORS 当前开发态全开，生产需收紧。
- Flowable 凭据、Keycloak client secret 均在配置中，交付时注意脱敏。

## 11. BPMN 编写要点（新增流程必读）

1. `userTask id` 必须 = slotConfig 的 `taskDefinitionKey`。
2. **首节点 assignee 用 `${starterAssignee}`，当前代码不自动注入**——启动请求必须在 `businessVariables` 里显式传 `starterAssignee`（这是集成最常见踩点）。
3. 普通节点**不再**配 HTTP ServiceTask 回调；节点通知由流程中心按 slotConfig `callbackUrl` 主动发送。
4. 末尾保留唯一一个 `stXX_framework_callback` 异步 HTTP ServiceTask（配置见 §9），requestBody 表达式含 processInstanceId/businessId/processDefinitionKey。
5. 多实例节点用 `multiInstanceLoopCharacteristics` + `collection`/`elementVariable`；`completionCondition >= 1` 为**或签**（一人通过即结束），`== nrOfInstances` 或不配为**会签**。多实例人员列表来自 slot 的 `variableName`（mode=multiple 写 list）。
6. 网关分支变量（如 `IS_SOLVED`、`PROBLEM_ATTRIBUTE`）由完成任务时的 `businessVariables` 提供；直接结束路径无需选人槽，可选配 no-op 展示槽（`required=false` + `users=[]`，不写变量）。
7. `businessType → processDefinitionKey` 映射在 `appsettings.json` 的 `BusinessTypeProcessMapping:Mappings`，新增业务类型需加映射并重启（无兜底推导）。

## 12. 业务系统集成步骤（交付 Codex 的执行清单）

以"某业务系统 X 接入流程中心"为例：

1. **设计流程**：产出 BPMN（按 §11 约定）+ slotConfigJson（每个 userTask 一项，slots 声明本节点完成时要为下游选的人）。
2. **部署**：`POST /api/flowable/bpmn/deploy`，用 `GET /api/flowable/bpmn/{key}/nodes` 验证 roleKey/callbackUrl/slots 落库。
3. **注册映射**：在流程中心 `BusinessTypeProcessMapping` 加 `businessType → processDefinitionKey` 并重启。
4. **发起**：`POST /api/processes/start`，body 含 `requestId`（生成并持久化，重试复用）、`businessType/businessId/businessTitle`、`initialSlotSelections`（按 slotKey）、`businessVariables.starterAssignee`、可选 `assigneeContract.roles`（推荐人池）、`callback.url`（流程级兜底）。
5. **待办渲染**：前端轮询/刷新 `GET /api/tasks/pending`，按 `requiredSlots` + `slotRecommendedUsers` 渲染选人区，按 `pageCode/pageUrl` 路由表单，按 `canReject/rejectOptions/canReassign` 渲染按钮；条件槽由前端结合业务选项与 `conditionalOn` 自行显隐（后端不过滤）。
6. **办理**：`POST /api/tasks/complete`，`action=1` 附 `nextSlotSelections`（按 slotKey）与网关 `businessVariables`；驳回 `action=2` 附 `rejectCode/rejectReason`；并行节点带 `taskId`。
7. **接收回调**：业务系统暴露回调端点，处理 `NODE_COMPLETED` / `REJECT_OCCURRED` / `PROCESS_COMPLETED` 三类 payload（§8）；按 `businessId + lastAuditRecord` 推进本地单据状态；注意节点回调无重试、可能重复，需幂等设计。
8. **查询与展示**：进度 `/progress`、流程图 `/flow-render`（有坐标用 bpmnXml，无坐标退化 dagre）、历史 `/audit-history`、状态 `/status`、列表 `/api/processes`。
9. **运维兜底**：失败投递看板走 `/api/admin/failed-deliveries`（retry / terminate-process）；ES 孤儿实例（`PROCESS_METADATA_INDEX_ORPHAN` + `[ES_WRITE_ORPHAN]` Critical 日志）人工修复。

## 13. 已知边界与限制（V3，设计上接受的取舍）

- **ES 孤儿实例**：启动时 ES 写失败仅 1 次重试，无对账 Job，需人工（V4 以 Outbox + 对账根治）。
- **节点回调无重试/无幂等键**：失败只记日志；业务系统需容忍重复与丢失。
- **多实例人员运行时不可增减**，只能转派；RoleAssignment 层不支持 conditionalOn（slot 层支持）。
- **不支持 NodeOverride**（运行时改单节点行为）；不支持 Saga/补偿、fallback 自动补人。
- **推荐范围不强制**：`restrictToRecommended` 仅审计越界，不拦截。
- **审计写入失败吞异常**（不阻塞主流程）；slot mode 与提交不匹配时静默跳过（仅 Warning）。
- **消息中心通知尽力而为**，失败不影响流程。
- 已交付 15 个业务流程 BPMN（人员选调、问题归零、劳动竞赛系列、门户资讯审批、巡察系列等），e2e 验证以问题归零三网关分支为准（2026-07-10 报告：全通过）。

## 14. V4 演进方向（预留的契约兼容点）

- Failed Delivery 中性契约不变，V4 可由 **Outbox Provider** 实现同一查询契约，但与 Flowable 重试状态机不共用；V4 Outbox DeadLetter 通常只返回 `retry_delivery`，不返回 `terminate_process`。
- Phase 2+ 根治项：Outbox Worker + ES 对账 Job、回调重试队列 + 幂等键、动态 LoopItem、NodeOverride、可选的后端强校验推荐范围。

## 15. 配置参考（`appsettings.json` 关键节）

| 配置节 | 关键项 | 说明 |
|---|---|---|
| `Jwt` | `Mode`（TrustedJwt/Oidc）、`UseridClaim`、`FallbackUseridClaims` | 认证模式与工号 claim |
| `ElasticSearch` | `Uri`、`IndexName`、`AuditIndexName`、`SemanticIndexName` | 三索引名 |
| `Flowable` | `BaseUrl`（flowable-rest）、账号、`TimeoutSeconds`、`FrameworkCallbackUrl` | 引擎接入 + 末尾回调入口（必须指向本服务 `/api/callback/flowable`） |
| `BusinessTypeProcessMapping:Mappings` | businessType → processDefinitionKey | 新业务类型注册点 |
| `Redis` | `ConnectionString` | 分布式锁 |
| `ProcessNotification` | `Enabled`、`MessageCenterBaseUrl/SendPath`、Keycloak token 端点与 client、`TaskUrlTemplate` | 消息中心通知 |

> 生产 `Flowable__FrameworkCallbackUrl` 必须是流程中心自身可达地址（示例环境为 `http://127.0.0.1:5013/api/callback/flowable`），否则末尾回调链路断裂。

---

## 附：文档地图

| 文档 | 内容 |
|---|---|
| `README.md` | 完整 API 测试规范（Patch Plan V1.3），含全部请求/响应 JSON、错误场景矩阵、端到端测试流程 |
| `specs/mian/spec.md` `plan.md` `tasks.md` | V1.1/V1.2 交互节点补丁的需求/方案/任务分解 |
| `specs/mian/process_center_patch_plan_v1.md` | 补丁总设计：四条人员原则、禁止项清单、已知限制 L1-L8 |
| `docs/superpowers/specs/2026-08-31-v3-failed-delivery-design.md` | 失败投递管理设计 |
| `docs/test-reports/2026-07-10-problem-zero-e2e-test-report.md` | 问题归零 e2e 测试结论 |
| `bpmn/` | 15 个已交付流程的 BPMN 源文件与部署响应存档 |
