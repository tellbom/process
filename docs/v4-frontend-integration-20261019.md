# V4 前端对接与 V3 对照核查

核查日期：2026-10-19。后端 `E:\process\process`，前端 `E:\Web\flow\web`，V3 对照源码 `E:\process\process-v3`。

后续已扩展为运维界面，增加收回、任务转派及投递处理入口，见 `docs/process-operations-20261019.md`。本文记录首次对接时的只读范围和验证快照；最新后端测试为 63 通过、12 跳过。

## 交付入口

- 开发页面：`http://192.168.124.5:1640/flow/allProcesses`。
- 页面组件：`/src/views/backend/todo/AllProcesses.vue`。
- 已在当前 RBAC 的 `flow` 项目创建并回读确认根菜单：`ruleCode=allProcesses`、`permissionCode=menu:allProcesses`、`path=allProcesses`、`menuType=Tab`、`weigh=3`。沿用既有 RBAC 动态路由，没有增加本地授权旁路或授予其他账号权限。
- 前端开发代理 `/process-api` 指向本机 `http://localhost:5012`；V4 后端以 Development 启动。生产配置中的流程服务占位域名改为 `/process-api`，部署服务器需要提供相同路径的反向代理。

## 页面行为

独立导航“全部流程”，不默认限定当前发起人。按业务 ID 精确匹配、业务类型、流程状态、发起人工号、发起日期筛选，支持重置、刷新和每页 20/50/100 条。日期按浏览器本地自然日取范围并转换为 UTC，包含结束日。

列表展示业务标题、业务 ID、流程定义与版本、审批轮次、状态、发起人、起止时间。每一轮审批使用独立 `processInstanceId`；查看详情、流程图、审计记录、历史表单上下文均传该实例，历史定义版本已知时使用对应版本的 BPMN。列表只读，行政终止操作仍在原申请页面。

## 差异与修复

| 核查点 | 原状 / V3 对照 | 本次处理 |
| --- | --- | --- |
| 全部流程独立页面 | 前端只有首页近期流程、我的待办、我的申请和失败投递管理 | 新增页面与真实 RBAC 菜单，首页“查看全部”和列表详情进入该页面 |
| 流程总数及深分页 | ES 未开启精确总数，超过 10,000 条计数截断，普通深分页受窗口限制 | `track_total_hits=true`；可选 `useCursor/cursor`，按创建时间和实例 ID 稳定排序；返回 `hasMore/nextCursor` |
| 同业务多轮详情 | 既有查询只按 businessId 获取最新一轮 | 进度、审计、流程图增加可选 processInstanceId，并校验业务与实例一致；原调用继续兼容 |
| 历史 BPMN 版本 | 流程图总是取最新定义 | 记录存在版本号时查询该版本；旧记录版本未知时保留原降级逻辑 |
| 待办深链 | V3 支持 TaskId、BusinessId；V4 DTO 和查询缺失 | V4 增加按任务精确定位并校验办理人/候选人，业务 ID 先解析实例再交给引擎过滤 |
| 待办过滤分页 | V4 页内筛选有 totalIsExact/hasMore，前端未消费 | 非精确计数显示“本页匹配”，以引擎 hasMore 翻页；遗漏未绑定投影的任务时也不再宣称总数精确 |
| 无效筛选 | 待办关键词、优先级、日期筛选没有对应后端能力；申请日期未传参 | 待办改为确有后端支持的业务 ID、业务类型；申请日期接入真实参数，业务 ID 明确标记精确匹配 |
| 历史审计完整性 | V4 审计和 Flowable 历史任务各只取前 200 条；V3 已有历史任务分页 | 实例审计在 ES 端过滤并 search_after 读取全部页；移入 V3 历史任务分页逻辑，提前结束时报错 |
| 从旧申请撤回 | terminate 按 businessId 解析最新轮次，旧列表可能误操作最新一轮 | 前端传所选实例，后端不一致时返回 PROCESS_INSTANCE_CHANGED，写操作前拒绝 |
| 启动契约 | 前端 StartProcessRequest 缺少必需 requestId 以及实例节点说明 | 补齐 requestId、businessTitle、assigneeContract.nodeDescriptions；重试复用 ID，新轮次换 ID |
| BPMN 响应 | BPMN controller 返回原始 DTO，其他 controller 包 ApiResult；前端统一按封装解析 | 增加显式 raw 模式，节点、部署、启动门禁、部署状态封装按实际契约消费 |
| 请求与状态展示 | 并发 loading 在异常时重复扣减，后端错误码/原因丢失，回调失败无中文状态 | loading 在 finally 只关闭一次；显示后端错误消息；请求前刷新已有 Keycloak 会话；补回调失败中文及颜色 |

## 新增 / 扩展的 API 契约

```text
GET /api/processes?useCursor=true&pageIndex=1&pageSize=20
GET /api/processes?useCursor=true&cursor=<nextCursor>&pageIndex=2&pageSize=20
    data.items / total / pageIndex / pageSize / hasMore / nextCursor

GET /api/processes/{businessId}/progress?processInstanceId=...
GET /api/processes/{businessId}/audit-history?processInstanceId=...
GET /api/processes/{businessId}/flow-render?processInstanceId=...

GET /api/tasks/pending?employeeId=...&taskId=...
GET /api/tasks/pending?employeeId=...&businessId=...

POST /api/processes/terminate
    { businessId, processInstanceId?, reason }
```

改变筛选条件或每页数量必须重新从第一页获取游标。此处是实时查询，不是冻结快照；查询期间新增和更新记录可通过刷新重新获取。

## 验证记录

- `dotnet build process.csproj --no-restore`：成功，0 错误，保留已有依赖警告。
- `dotnet test Test\FlowableWrapper.Test.csproj --no-restore`（独立输出目录）：54 通过，12 跳过，0 失败。跳过项为未启用配置的 DM8 集成测试，不能视为 DM8 完整验收。
- `npm run build`：成功；已有依赖和大包提示仍在。
- `npm run typecheck`：改前 41 处、改后 41 处，逐行差异 0；错误来自原有 RBAC 等模块，不能称全项目类型检查通过。
- 使用提供的账号完成真实 Keycloak 登录、RBAC 初始化并读取真实 V4 数据。测试会话仅在临时浏览器内存中，没有保存密码、Token 或登录状态文件。
- 全部流程真实计数为 10,185；从偏移 9,980 的第 500 页取得游标后，第 501 页返回 20 条，边界两页实例没有重复。
- 浏览器“下一页 / 上一页”、业务 ID 筛选、无结果与禁用下一页、重置恢复 10,185 条均通过。
- 同一业务第 2、3、4、5 轮分别请求进度及流程图，返回实例均与所选轮次一致；浏览器打开第 4 轮后展示对应实例与历史意见。
- 日期筛选实际返回当天的 6 条记录；BPMN 节点数组与 startability 原始 DTO 能正确读取。
- 当前用户最旧待办不在首两条任务内，taskId 精确查询与浏览器深链仍能定位并打开审批抽屉；不存在的任务返回空结果。
- 浏览器测试未发现 JavaScript 页面异常。没有提交真实业务审批、驳回、转派、撤回、重试或取消投递。

## 仍需区分的边界

1. 当前原配置 `CallbackWorker.Enabled=false`，生产 readiness 会要求回调 Worker 初始化。本次开发联调沿用 Development 的 readiness 设置，没有启动 Worker 消费已有队列。此结果证明页面和查询对接，不证明回调投递及生产启动验收。
2. 流程列表使用 ES 展示投影；发现有已产生多条审批历史但列表仍标为 running 的记录。不能根据审批意见自行改成 completed；需要检查业务完成回调、DM8 状态及投影对账后处理。
3. V3 独有的匿名 `GET /api/processes/instances/{processInstanceId}/approval-details` 在 V4 中仍不存在。本次页面用受认证保护的实例查询参数完成隔离，没有移入匿名访问行为。若业务系统依赖该专用 API，需要另做调用方和部署访问边界对齐。
4. BPMN 管理、业务系统自己的发起表单、后台迁移管理没有在本次扩展成新页面。流程前端没有现存 startProcess 业务调用者，requestId 类型补齐不等于外部业务系统已完成升级。
5. V3 和两仓库已有未提交改动均保留。本次没有提交或推送 Git，也没有修改账号权限、开启回调 Worker 或替换其他业务服务。

