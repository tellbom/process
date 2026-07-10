# 问题归零流程端到端测试报告

## 1. 测试结论

测试通过。`problem_zero` 的三个网关结果均已通过真实 API 启动独立流程实例并运行至 `completed`，覆盖全部 6 个用户节点。共验证 14 次节点级回调和 3 次流程级回调，回调均通过本机 WLAN 地址 `192.168.124.5` 完成，未使用 `localhost`，未发现网络访问或 CORS 阻断。

本结论限于本报告所列问题归零主流程分支、部署、查询、节点完成和回调能力。驳回、转派、终止等通用异常操作不属于“所有 BPMN 网关分支”的本次验收范围。

## 2. 环境与配置

| 项目 | 实际值 | 结果 |
|---|---|---|
| 测试时间 | 2026-07-10 09:21–09:26（Asia/Shanghai） | 已记录 |
| API 地址 | `http://192.168.124.5:5012` | 可访问 |
| API 监听 | `http://0.0.0.0:5012` | 正常 |
| Flowable | `192.168.124.2:18080` | TCP 可达、API 调用正常 |
| Elasticsearch | `192.168.124.2:19200` | TCP 可达、索引初始化正常 |
| Redis | `192.168.124.2:16379` | TCP 可达、流程操作正常 |
| 节点回调 | `http://192.168.124.5:5012/api/test/node-callback` | 6 个节点均已配置 |
| 流程回调 | `http://192.168.124.5:5012/api/test/process-callback` | 每个启动请求均已配置 |
| 认证模式 | `TrustedJwt`，测试 JWT 含 `userid` | 正常 |

配置准备：

- `problem_zero_slot.json` 中 6 个 `callbackUrl` 从显式 `null` 改为上述节点回调 URL。
- `BusinessTypeProcessMapping.Mappings` 增加 `problem_zero -> problem_zero`，与 README 当前有效说明一致。
- API 从非 localhost 地址完成健康检查、部署、启动、待办、完成、进度、状态、审计、流程图和回调查询。

## 3. 构建、网络与部署结果

| 检查项 | 实际结果 | 判定 |
|---|---|---|
| `dotnet test Test/FlowableWrapper.Test.csproj --no-restore` | 退出码 0；项目未输出用例统计 | 通过 |
| `dotnet build process.csproj --no-restore` | 0 错误，1 警告 | 通过（有风险提示） |
| LAN 健康检查 `GET /api/test` | `ok=true` | 通过 |
| 外部 Origin CORS 预检 | HTTP 204，`Access-Control-Allow-Origin: *`，允许 Authorization | 通过 |
| BPMN 部署 | deploymentId `b7e6e1f5-7bfd-11f1-a767-0242ac110004` | 通过 |
| 流程定义 | key=`problem_zero`，name=`问题归零`，version=5 | 通过 |
| 节点配置查询 | 6 个节点，6 个 LAN `callbackUrl` | 通过 |

构建警告：`System.IO.Hashing 9.0.12` 声明不支持/未测试 `net6.0`，建议后续升级目标框架到 `net8.0` 或调整依赖版本。该警告未阻塞本次构建和运行，但应作为技术债跟踪。

## 4. BPMN 分支覆盖

| 场景 | 网关变量 | 实际节点序列 | 最终状态 | 判定 |
|---|---|---|---|---|
| 已解决，直接结束 | UT-03: `IS_SOLVED=true` | UT-01 → UT-02 → UT-03 | completed | 通过 |
| 未解决，专项工作 | UT-03: `IS_SOLVED=false`; UT-04: `PROBLEM_ATTRIBUTE=true` | UT-01 → UT-02 → UT-03 → UT-04 → UT-05 → UT-06 | completed | 通过 |
| 未解决，非专项工作 | UT-03: `IS_SOLVED=false`; UT-04: `PROBLEM_ATTRIBUTE=false` | UT-01 → UT-02 → UT-03 → UT-04 → UT-06 | completed | 通过 |

测试业务 ID：

- `PZ_E2E_SOLVED_20260710092431`
- `PZ_E2E_SPECIAL_20260710092431`
- `PZ_E2E_NONSPECIAL_20260710092431`

三个实例的 `currentNodes` 最终均为空；审计历史顺序与 BPMN 条件路径完全一致。专项路径包含 UT-05，非专项路径确认未经过 UT-05。三个场景合并后，UT-01 至 UT-06 全部至少覆盖一次，两个排他网关的 true/false 出边均已覆盖。

## 5. 回调验证

| 场景 | 节点回调 | 流程回调 | 总数 | 顺序/节点匹配 |
|---|---:|---:|---:|---|
| 已解决 | 3 | 1 | 4 | 匹配 |
| 未解决、专项 | 6 | 1 | 7 | 匹配 |
| 未解决、非专项 | 5 | 1 | 6 | 匹配 |
| 合计 | 14 | 3 | 17 | 全部匹配 |

节点回调记录的 `callbackType` 均为 `NODE_COMPLETED`，`taskDefinitionKey` 和 `nodeSemantic` 与实际审计节点一致。流程回调记录的 `callbackType` 均为 `PROCESS_COMPLETED`，且对应业务 ID 和流程实例 ID 正确。应用日志同时记录每次 `[NODE_COMPLETED]` 回调 HTTP 状态码为 200。

专项路径观测到流程完成回调先于最终 UT-06 节点通知进入测试控制器（约 0.28 秒），两者最终均成功。这是异步/调用链时序现象；业务接收方不应假定“最后节点通知一定先于流程完成通知”。本次不影响完整性判定。

## 6. 测试过程偏差与处置

测试脚本初版按 `/api/tasks/pending` 的 `nodeId` 定位任务，但该 DTO 实际只返回 `nodeSemantic`。首个诊断实例 `PZ_E2E_SOLVED_20260710092343` 已成功启动，但未完成任何用户任务；确认根因后，脚本改为从 `/progress` 使用 `nodeId` 精确取得 `taskId`，再验证该 taskId 属于指定用户待办。诊断实例随后通过终止 API 安全结束，相关进度和终止响应已留证。此偏差属于测试工具问题，不是被测流程缺陷。

## 7. 验收意见

基于本次范围，可以确认：

- 问题归零 BPMN 与槽位 JSON 可以成功部署。
- 选人槽正确写入下一节点所需 Flowable 变量。
- `IS_SOLVED` 和 `PROBLEM_ATTRIBUTE` 能正确驱动全部网关路径。
- 所有用户节点都能完成并形成正确审计记录。
- 节点级和流程级回调都能通过本机真实 IP 回调到本项目 `TestController`。
- 从模拟外部 Origin 访问未受到当前 CORS 策略阻断。

建议验收结论：**问题归零主流程及回调链路通过，可确认本次测试范围内无阻断性问题。**

## 8. 证据索引

- 部署响应：`evidence/problem-zero/deploy.json`
- 部署节点查询：`evidence/problem-zero/deployed-nodes.json`
- LAN/CORS：`evidence/problem-zero/lan-health-cors.json`
- 场景 ID：`evidence/problem-zero/scenario-ids.json`
- 综合核验：`evidence/problem-zero/verification-summary.json`
- 各场景：`solved-*`、`special-*`、`nonspecial-*`（status/progress/audit-history/flow-render/callbacks）
- 应用日志：`evidence/problem-zero/api.stdout.log`、`api.stderr.log`
- 诊断实例：`diagnostic-aborted-progress.json`、`diagnostic-aborted-terminate.json`
- 可复现脚本：`Test/problem_zero_e2e.ps1`
