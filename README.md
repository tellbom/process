# 流程中心 API 测试规范

> **版本**：Patch Plan V1.3（Slot 三键分离）
> **栈**：FlowableWrapper .NET 6 + Flowable 7.2 + Elasticsearch + Redis + DM8

## 压测用 A/B 完成回调

`TestController` 提供末节点业务回调模拟：

- `POST /api/test/process-callback/A`：立即返回；
- `POST /api/test/process-callback/B?delayMs=15000`：延迟返回；
- `POST /api/test/process-callback/mixed?delayMs=15000&slowPercent=50`：
  按 `businessId` 稳定混合分组；
- `GET /api/test/callback-metrics`：返回 A/B 数量、当前并发和峰值并发。

前端可直接使用的指标响应示例：

```json
{
  "ok": true,
  "retainedRecords": 20,
  "totalProcessCallbacks": 20,
  "fastProcessCallbacks": 10,
  "slowProcessCallbacks": 10,
  "activeProcessCallbacks": 0,
  "maxActiveProcessCallbacks": 5
}
```

完整 k6 参数和阶梯执行方法见 `performance/README.md`。
> **用途**：交付测试 Agent 执行完整端到端测试
> **原则**：Flowable 是唯一真相层；流程中心是映射层、包装层、审计层；`NextSlotSelections` 是唯一最终生效人员来源；`slotKey` / `roleKey` / `variableName` 三者职责分离，禁止互相推导

---

## 0. 公共约定

### 0.1 请求头

| Header | 必填 | 说明 |
|---|---|---|
| `Content-Type: application/json` | POST 必填 | — |
| `X-User-Id: {employeeId}` | 视接口 | 操作人工号；不使用 `Authorization` |

### 0.2 统一响应结构

所有接口 HTTP 状态码统一 200，通过 `success` 区分业务成功与失败。

```json
{ "success": true,  "message": "操作成功", "data": {} }
{ "success": false, "message": "错误描述", "errorCode": "ERROR_CODE" }
```

### 0.3 错误码速查

| 错误码 | 含义 |
|---|---|
| `FLOWABLE_START_FAILED` | Flowable 启动失败 |
| `PROCESS_METADATA_INDEX_ORPHAN` | 流程已启动但 ES 两次写入均失败 |
| `REJECT_CODE_REQUIRED` | 驳回未传 rejectCode |
| `REJECT_REASON_REQUIRED` | 驳回未传 rejectReason |
| `REJECT_NOT_ALLOWED` | 当前节点 canReject=false |
| `REASSIGN_NOT_ALLOWED` | 当前节点未配置 canReassign=true |
| `REJECT_CODE_INVALID` | rejectCode 不在 rejectOptions 中 |
| `REJECT_TARGET_NOT_FOUND` | 找不到驳回目标节点 |
| `METADATA_NOT_FOUND` | ES 元数据不存在（触发 Flowable 重试） |
| `SLOT_ROLE_KEY_REQUIRED` | slot 缺少 roleKey |
| `SLOT_VARIABLE_NAME_REQUIRED` | slot 缺少 variableName |
| `SLOT_KEY_INVALID` | 请求提交了未知 slotKey |

### 0.4 日志关键字

| 关键字 | 级别 | 含义 |
|---|---|---|
| `[ES_WRITE_FAIL]` | Error | ES 首次写入失败，开始重试 |
| `[ES_WRITE_RETRY_SUCCESS]` | Warning | ES 重试成功 |
| `[ES_WRITE_ORPHAN]` | Critical | ES 两次均失败，孤儿实例 |
| `[RECOMMEND_RANGE_EXCEEDED]` | Warning | 提交推荐范围外人员，审计不拦截 |
| `[NODE_COMPLETED] 节点回调成功` | Information | 节点完成回调发送成功 |
| `[REJECT_OCCURRED] 节点回调成功` | Information | 驳回通知发送成功 |

---

## 1. 前置：BPMN + slotConfig 部署

### 1.1 部署

**POST** `/api/flowable/bpmn/deploy`
`Content-Type: multipart/form-data`

| 字段 | 类型 | 说明 |
|---|---|---|
| `file` | File | `.bpmn` 文件 |
| `slotConfigJson` | String | 节点配置 JSON 数组 |

**slotConfig 节点字段**：

| 字段 | 类型 | 说明 |
|---|---|---|
| `taskDefinitionKey` | string | 必须与 BPMN userTask id 一致 |
| `nodeSemantic` | string | 业务语义，前端据此路由表单组件 |
| `pageCode` | string | 前端渲染地址 |
| `roleKey` | string | 业务角色 Key，对应 AssigneeContract.roles[].roleKey |
| `assigneeMode` | string | `single` / `multiple` |
| `callbackUrl` | string | 节点级回调 URL；显式 null/空表示禁用节点通知，未声明才降级到 callback.url |
| `canReassign` | boolean | 当前节点是否允许转派；只有显式 `true` 才开放，未配置默认为 `false` |
| `canReject` | bool | 当前节点是否可驳回 |
| `rejectOptions` | array | 驳回目标列表，含 `rejectCode` / `label` / `description` |
| `isRejectTarget` | bool | 是否可作为驳回落点 |
| `rejectCode` | string | 本节点作为驳回落点时的标识 |
| `slots` | array | 选人槽位定义 |

`callbackUrl` 随节点语义写入 ES 索引 `flowable-process-definition-semantic`，实际位于 `_source.nodeSemanticMap.<taskDefinitionKey>.callbackUrl`。`nodeSemanticMap` 的 key 是动态节点 ID，不是关系库固定列；索引映射对该对象使用 `dynamic:false`，完整 JSON 仍保留在 `_source` 并可按流程定义 Key 整体读取，但不会再为每个节点 ID 创建 ES 字段，因此不会持续消耗默认 1000 个 mapping fields。

**slot 字段**：

| 字段 | 类型 | 说明 |
|---|---|---|
| `slotKey` | string | 前端提交选人时使用的槽位标识，全流程唯一 |
| `roleKey` | string | 该 slot 的推荐池来源角色；后端从 `RecommendedAssigneesSnapshot[slot.roleKey]` 取候选人 |
| `label` | string | 前端展示标签 |
| `mode` | string | `single` / `multiple` |
| `variableName` | string | 最终写入 Flowable 的流程变量名；不作为前端提交 key |
| `required` | bool | 是否必填 |
| `conditionalOn` | string | 条件表达式（如 `needPersonFeedback==true`），满足时才需填 |
| `restrictToRecommended` | bool | `true` = 建议前端限制该 slot 的可选范围；后端记录越界审计，不强拦截 |

**Slot 三键硬约定**：

- `slotKey`：前端提交 `nextSlotSelections` / `initialSlotSelections` 时的 key。
- `roleKey`：推荐候选人从哪个角色池取，查询链固定为 `slot.roleKey -> RecommendedAssigneesSnapshot[roleKey]`。
- `variableName`：后端将选人结果写入 Flowable 的变量名。
- 三者禁止互相替代，禁止按命名规则猜测；slot 缺少 `roleKey` 或 `variableName` 时视为配置错误。

**成功响应**：

```json
{
  "success": true,
  "data": {
    "deploymentId": "deploy-uuid-001",
    "processDefinitionKey": "personnel_selection_approval",
    "nodes": [
      {
        "taskDefinitionKey": "ut00_starter_submit",
        "nodeSemantic": "STARTER_SUBMIT",
        "pageCode": "https://httpbin.org/get?node=starter_submit",
        "roleKey": "starter",
        "assigneeMode": "single",
        "callbackUrl": "https://httpbin.org/post?node=starter_submit",
        "slotCount": 1
      }
    ]
  }
}
```

### 1.2 验证部署结果

**GET** `/api/flowable/bpmn/{processDefinitionKey}/nodes`

确认所有节点的 `roleKey`、`callbackUrl`、`slots` 已正确写入 ES。

---

## 2. 启动流程

**POST** `/api/processes/start`
`X-User-Id: EMP_START`

### 字段说明

| 字段 | 职责 |
|---|---|
| `initialSlotSelections` | 首节点选人 → 生成 Flowable 启动变量（执行路径） |
| `assigneeContract` | 按 roleKey 传推荐人 → 写入 RecommendedAssigneesSnapshot（展示用，不影响执行） |
| `businessVariables` | 网关条件变量、starterAssignee 等 → 直接注入 Flowable |
| `callback.url` | 流程级回调地址，仅在节点未声明 callbackUrl 时作为兼容降级使用 |

`RecommendedAssigneesSnapshot` 的 Key 是业务角色 `roleKey`，由 `assigneeContract.roles[]` 固化而来。当前节点自己的 `roleKey` 表示“谁处理当前节点”；slot 里的 `roleKey` 表示“这个选人槽从哪个推荐池取候选人”。两者字段名相同但主体不同，不能用当前节点 `roleKey` 代替 slot 推荐人组装。

启动和完成任务时，前端只用 `slotKey` 提交选人；后端根据 slotConfig 找到 `SlotDefinition`，再写入 `slot.variableName` 对应的 Flowable 变量。未知 `slotKey` 直接返回错误，不再兼容旧版本的静默忽略。

### 2.1 半自动流程（前端每步选人，assigneeContract 提供推荐）

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "SEMI_AUTO_001",
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_001"] }
  ],
  "assigneeContract": {
    "roles": [
      { "roleKey": "inspection_office_reviewer", "users": ["EMP_005"] },
      { "roleKey": "integrity_dept_reviewer",    "users": ["EMP_010"] },
      { "roleKey": "integrity_head",             "users": ["EMP_015"] },
      { "roleKey": "office_director",            "users": ["EMP_020"] },
      { "roleKey": "secretary",                  "users": ["EMP_025"] }
    ]
  },
  "businessVariables": {
    "starterAssignee": "EMP_START",
    "needPersonFeedback": false
  },
  "callback": { "url": "https://httpbin.org/post", "timeoutSeconds": 30 }
}
```

**验证点**：
- `data.firstNodeSemantic` = `STARTER_SUBMIT`
- ES `RecommendedAssigneesSnapshot` 含各 roleKey → users 映射
- Flowable 变量 `groupLeaderAssignee = "EMP_001"` 已注入

### 2.2 半自动流程（无推荐人）

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "SEMI_NO_RECOMMEND_001",
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_001"] }
  ],
  "businessVariables": { "starterAssignee": "EMP_START", "needPersonFeedback": false },
  "callback": { "url": "https://httpbin.org/post" }
}
```

**验证点**：`GET /progress` 中 `currentNodes[].slotRecommendedUsers = {}`

### 2.3 全自动流程（assigneeContract 提供全流程推荐，配合 restrictToRecommended 锁定选人）

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "FULL_AUTO_001",
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_001"] }
  ],
  "assigneeContract": {
    "roles": [
      { "roleKey": "inspection_office_reviewer", "users": ["EMP_005"] },
      { "roleKey": "integrity_dept_reviewer",    "users": ["EMP_010"] },
      { "roleKey": "integrity_head",             "users": ["EMP_015"] },
      { "roleKey": "office_director",            "users": ["EMP_020"] },
      { "roleKey": "secretary",                  "users": ["EMP_025"] }
    ]
  },
  "businessVariables": { "starterAssignee": "EMP_START", "needPersonFeedback": false },
  "callback": { "url": "https://httpbin.org/post" }
}
```

**验证点**：
- ES `RecommendedAssigneesSnapshot` 含全流程所有 roleKey 推荐人
- `GET /progress` `currentNodes[].slotRecommendedUsers[slotKey]` 有值
- `restrictToRecommended=true` 的 slot，`currentNodes[].restrictToRecommended[slotKey] = true`

### 2.4 无推荐人启动路径

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "LEGACY_001",
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_001"] }
  ],
  "businessVariables": { "starterAssignee": "EMP_START" },
  "callback": { "url": "https://httpbin.org/post" }
}
```

### 2.5 成功响应

```json
{
  "success": true,
  "data": {
    "processInstanceId": "proc-uuid-001",
    "businessId": "SEMI_AUTO_001",
    "firstTaskId": "task-uuid-001",
    "firstNodeSemantic": "STARTER_SUBMIT",
    "firstPageCode": "https://httpbin.org/get?node=starter_submit"
  }
}
```

### 2.6 错误场景

| 场景 | 预期 |
|---|---|
| `businessId` 重复（已有 running 流程） | `success: false` |
| `businessType` 未配置映射 | `success: false` |
| `X-User-Id` 未传 | `success: false`（无法确定操作人） |
| `initialSlotSelections` 含未知 slotKey | `errorCode: SLOT_KEY_INVALID` |
| slotConfig 中 slot 缺少 roleKey / variableName | 部署或运行时失败 |
| Flowable 不可用 | `errorCode: FLOWABLE_START_FAILED` |
| ES 两次写入均失败 | `errorCode: PROCESS_METADATA_INDEX_ORPHAN` |

---

## 3. 查询待办（用户视角入口）

**GET** `/api/tasks/pending`
`X-User-Id: EMP_001`

Pending task responses include `slotRecommendedUsers` keyed by `slotKey`, `restrictToRecommended` keyed by `slotKey`, and `pageUrl` when `pageCode` is an http/https URL. `requiredSlots[]` includes `slotKey` / `roleKey` / `variableName` so the frontend can render by slot and submit by `slotKey`.

流程中心不解析 BPMN gateway 来预测后续路径。排他网关、并行网关场景下，如果当前节点需要提前选择多个下游处理人，必须在当前节点 `slots` 中显式声明多个选人槽；`/api/tasks/pending` 只返回这些显式声明的 requiredSlots 及其推荐人。

| 参数 | 类型 | 说明 |
|---|---|---|
| `employeeId` | string | 优先于 Header |
| `businessType` | string[] | 按业务类型过滤（可选）；重复传参，多个值按 OR 匹配 |
| `pageIndex` | int | 默认 1 |
| `pageSize` | int | 默认 20 |

**示例**：`GET /api/tasks/pending?employeeId=EMP_001&businessType=type_a&businessType=type_b&pageIndex=1&pageSize=20`

**响应**：

```json
{
  "success": true,
  "data": {
    "items": [
      {
        "taskId": "task-uuid-001",
        "taskName": "巡察组组长确认",
        "businessId": "SEMI_AUTO_001",
        "businessType": "personnel_selection_approval",
        "nodeSemantic": "GROUP_LEADER_CONFIRM",
        "roleKey": "group_leader",
        "pageCode": "https://httpbin.org/get?node=group_leader_confirm",
        "pageUrl": "https://httpbin.org/get?node=group_leader_confirm&businessId=SEMI_AUTO_001&taskId=task-uuid-001&businessType=personnel_selection_approval&nodeId=ut01_group_leader_confirm&nodeSemantic=GROUP_LEADER_CONFIRM",
        "canReject": true,
        "canReassign": true,
        "rejectOptions": [
          { "rejectCode": "TO_STARTER", "label": "退回发起人重新提交" }
        ],
        "requiredSlots": [
          {
            "slotKey": "inspection_office_reviewer",
            "roleKey": "inspection_office_reviewer",
            "label": "巡察办审核人",
            "variableName": "inspectionOfficeReviewAssignee",
            "mode": "single",
            "required": true,
            "restrictToRecommended": false
          }
        ],
        "slotRecommendedUsers": {
          "inspection_office_reviewer": ["EMP_005"]
        },
        "restrictToRecommended": {
          "inspection_office_reviewer": false
        },
        "createTime": "2024-01-15T08:30:00Z"
      }
    ],
    "total": 1,
    "pageIndex": 1,
    "pageSize": 20
  }
}
```

**验证点**：
- `requiredSlots` 与 slotConfig 一致
- `slotRecommendedUsers` 的 key 必须来自 `requiredSlots[].slotKey`
- `canReject` / `rejectOptions` 与 slotConfig 一致
- 不属于该用户的任务不出现

---

## 4. 流程进度 / 流程图渲染

### 4.1 流程进度（含推荐人）

**GET** `/api/processes/{businessId}/progress`

**响应**：

```json
{
  "success": true,
  "data": {
    "businessId": "SEMI_AUTO_001",
    "processInstanceId": "proc-uuid-001",
    "processDefinitionKey": "personnel_selection_approval",
    "status": "running",
    "createdBy": "EMP_START",
    "createdTime": "2024-01-15T08:00:00Z",
    "completedTime": null,
    "currentNodes": [
      {
        "taskId": "task-uuid-001",
        "nodeId": "ut02_inspection_office_review",
        "nodeName": "巡察办审核",
        "nodeSemantic": "INSPECTION_OFFICE_REVIEW",
        "pageCode": "https://httpbin.org/get?node=inspection_office_review",
        "assignee": "EMP_005",
        "candidateUsers": [],
        "createTime": "2024-01-15T08:01:00Z",
        "slotRecommendedUsers": {
          "integrity_dept_reviewer": ["EMP_010"]
        },
        "restrictToRecommended": {
          "integrity_dept_reviewer": false
        }
      }
    ],
    "auditHistory": [
      {
        "taskDefinitionKey": "ut00_starter_submit",
        "nodeSemantic": "STARTER_SUBMIT",
        "action": "approve",
        "operatorId": "EMP_START",
        "comment": "提交申请",
        "operatedAt": "2024-01-15T08:00:30Z",
        "slotSelections": [
          { "slotKey": "group_leader", "label": "巡察组组长", "users": ["EMP_001"] }
        ]
      }
    ]
  }
}
```

**`currentNodes` 验证矩阵**：

| 场景 | `slotRecommendedUsers` | `restrictToRecommended` |
|---|---|---|
| 传了 `assigneeContract` | 按当前节点 slotConfig 的 `slots[].slotKey` 输出推荐人 | 按 slotConfig 的 slotKey 输出 |
| 未传 `assigneeContract` | `{}` | 所有 slot 均为 `false` |
| 流程已 completed | `currentNodes = []` | — |

### 4.2 渲染流程图

**GET** `/api/processes/{businessId}/flow-render`

**响应**：

```json
{
  "success": true,
  "data": {
    "businessId": "SEMI_AUTO_001",
    "bpmnXml": "<definitions>...</definitions>",
    "nodes": [
      { "id": "ut00_starter_submit", "name": "发起人提交", "type": "userTask", "x": 100, "y": 200, "width": 100, "height": 80 }
    ],
    "edges": [
      { "id": "flow_ut00_to_ut01", "sourceId": "ut00_starter_submit", "targetId": "ut01_group_leader_confirm" }
    ],
    "activeTaskRenders": [
      { "taskId": "task-uuid-001", "nodeId": "ut01_group_leader_confirm", "assignee": "EMP_001", "status": "active" }
    ],
    "completedRecords": [
      { "nodeId": "ut00_starter_submit", "operatorId": "EMP_START", "outcome": "approved", "comment": "提交申请", "round": 1 }
    ]
  }
}
```

**验证点**：
- `bpmnXml` 有值时前端按坐标渲染；为 null 时前端退化 dagre 自动布局
- `activeTaskRenders` 当前节点 `status = active`
- `completedRecords[].outcome` 合法值：`approved` / `rejected_return` / `reassigned`
- 转派后出现 `outcome = reassigned` 记录

### 4.3 审批历史

**GET** `/api/processes/{businessId}/audit-history`

### 4.4 流程状态（轻量）

**GET** `/api/processes/{businessId}/status`

`status` 合法值：`running` / `completed` / `terminated` / `callback_failed`

### 4.5 流程列表

**GET** `/api/processes?businessType=xxx&status=running&pageIndex=1&pageSize=20`

---

## 5. 完成任务（审批通过）

**POST** `/api/tasks/complete`
`X-User-Id: {当前处理人}`
`action = 1`

### 5.1 半自动流程通过（传 NextSlotSelections）

`NextSlotSelections` 是唯一最终生效人员来源。

```json
{
  "businessId": "SEMI_AUTO_001",
  "action": 1,
  "comment": "同意，人员配置合理",
  "nextSlotSelections": [
    { "slotKey": "inspection_office_reviewer", "users": ["EMP_005"] }
  ]
}
```

**验证点**：
- Flowable 变量 `inspectionOfficeReviewAssignee = "EMP_005"` 写入
- 审计记录 `action = approve`，`slotSelections` 含选人快照
- `GET /progress` `currentNodes` 推进到下一节点

### 5.2 全自动流程通过（推荐人确认后作为 NextSlotSelections）

`slotRecommendedUsers` 按 `slotKey` 返回当前节点 requiredSlots 的推荐人；`NextSlotSelections` 也必须按 `slotKey` 提交。前端不要用节点级 `roleKey` 或 `variableName` 作为提交 key，也不要再做 `slotKey/roleKey/variableName` 同名兜底。

候选人读取规则固定为：遍历 `requiredSlots[]`，使用 `slotRecommendedUsers[slot.slotKey] ?? []` 初始化该 slot 的候选人区域。

```json
{
  "businessId": "FULL_AUTO_001",
  "action": 1,
  "comment": "确认通过",
  "nextSlotSelections": [
    { "slotKey": "inspection_office_reviewer", "users": ["EMP_005"] }
  ]
}
```

### 5.3 提交推荐范围外人员（restrictToRecommended=true）

```json
{
  "businessId": "FULL_AUTO_001",
  "action": 1,
  "nextSlotSelections": [
    { "slotKey": "inspection_office_reviewer", "users": ["EMP_999"] }
  ]
}
```

**验证点**：
- 流程正常推进，**不拦截**
- 审计记录 `hasOutOfRecommendedRange = true`
- 日志 `[RECOMMEND_RANGE_EXCEEDED]`

### 5.4 附带网关条件变量

```json
{
  "businessId": "SEMI_AUTO_001",
  "action": 1,
  "businessVariables": { "needPersonFeedback": true },
  "nextSlotSelections": [
    { "slotKey": "feedback_person", "users": ["EMP_099"] }
  ]
}
```

### 5.5 并行节点（指定 taskId）

```json
{
  "businessId": "PARALLEL_001",
  "taskId": "task-uuid-branch-a",
  "action": 1,
  "comment": "分支 A 通过"
}
```

---

## 6. 驳回

**POST** `/api/tasks/complete`
`X-User-Id: {当前处理人}`
`action = 2`

### 6.1 正常驳回

```json
{
  "businessId": "SEMI_AUTO_001",
  "action": 2,
  "rejectCode": "TO_STARTER",
  "rejectReason": "材料不完整，请重新填写"
}
```

**验证点**：
- Flowable 跳回至 `rejectCode` 对应节点
- 审计记录 `action = reject`，`rejectReason` 正确写入
- 业务系统收到 `callbackType = REJECT_OCCURRED` 通知，含 `rejectTargetNodeKey`
- `GET /progress` `currentNodes` 变为驳回目标节点
- `GET /flow-render` `completedRecords` 含 `outcome = rejected_return`

### 6.2 驳回回调 Payload（流程中心主动发出）

```json
{
  "businessId": "SEMI_AUTO_001",
  "processInstanceId": "proc-uuid-001",
  "processDefinitionKey": "personnel_selection_approval",
  "businessType": "personnel_selection_approval",
  "callbackType": "REJECT_OCCURRED",
  "taskDefinitionKey": "ut02_inspection_office_review",
  "rejectTargetNodeKey": "ut00_starter_submit",
  "lastAuditRecord": {
    "action": "reject",
    "operatorId": "EMP_005",
    "comment": null,
    "rejectReason": "材料不完整，请重新填写",
    "operatedAt": "2024-01-15T10:00:00Z",
    "slotSelections": []
  },
  "triggeredAt": "2024-01-15T10:00:01Z"
}
```

### 6.3 错误场景

| 场景 | 预期错误码 |
|---|---|
| `rejectCode` 未传 | `REJECT_CODE_REQUIRED` |
| `rejectReason` 未传 | `REJECT_REASON_REQUIRED` |
| `canReject=false` | `REJECT_NOT_ALLOWED` |
| `rejectCode` 不在 `rejectOptions` | `REJECT_CODE_INVALID` |
| 找不到驳回目标节点 | `REJECT_TARGET_NOT_FOUND` |

---

## 7. 转派

**POST** `/api/tasks/reassign`
`X-User-Id: EMP_ADMIN`

转派由当前节点 slotConfig 的 `canReassign` 控制。只有 `canReassign=true` 时待办响应才返回可转派，前端显示按钮，后端接口也允许执行；未配置或为 `false` 时返回 `REASSIGN_NOT_ALLOWED`。因此发起人节点或其他不支持转派的节点无需配置该字段，不影响流程设计和正常审批。

转派只作用于当前节点当前 Task，不改变其他节点预设推荐人。

```json
{
  "businessId": "SEMI_AUTO_001",
  "newAssignees": ["EMP_006"],
  "reason": "原处理人请假",
  "operatorId": "EMP_ADMIN"
}
```

并行节点需指定 taskId：

```json
{
  "businessId": "PARALLEL_001",
  "taskId": "task-uuid-branch-a",
  "newAssignees": ["EMP_007"],
  "reason": "转派",
  "operatorId": "EMP_ADMIN"
}
```

**验证点**：
- `GET /tasks/pending?employeeId=EMP_006` 出现该任务
- `GET /tasks/pending?employeeId=EMP_001` 任务消失
- `GET /flow-render` `completedRecords` 含 `outcome = reassigned`
- `GET /progress` `currentNodes[].assignee = "EMP_006"`

---

## 8. 终止流程

**POST** `/api/processes/terminate`
`X-User-Id: EMP_ADMIN`

```json
{
  "businessId": "SEMI_AUTO_001",
  "reason": "业务取消，管理员手动终止"
}
```

**验证点**：`GET /status` 返回 `status = terminated`

---

## 9. 回调接口

**POST** `/api/callback/flowable`

> 由 BPMN 中保留的 Flowable HTTP ServiceTask 调用，非业务系统或前端直调。

### 9.1 流程结束回调（Flowable → 流程中心）

普通节点完成通知不再通过 BPMN 后置 HTTP ServiceTask 触发。BPMN 只保留最后一个流程完成契约回调，用于通知流程中心将实例状态收口为 completed，并向业务系统发送流程完成通知。

```json
{
  "processInstanceId": "proc-uuid-001",
  "businessId": "SEMI_AUTO_001",
  "processDefinitionKey": "personnel_selection_approval"
}
```

**验证点**：ES `status = completed`；幂等重复调用返回 200 不重复通知。

### 9.2 节点完成回调（流程中心 → 业务系统）

节点完成后，流程中心在 `CompleteTaskAsync` 成功调用 Flowable `CompleteAsync` 之后主动发送业务回调。触发依据是当前节点 slotConfig 中的 `callbackUrl`：有效 URL 发送节点级回调；显式 null/空直接跳过；未声明时才降级使用启动时 `callback.url`。

| callbackType | 触发时机 | BPMN ServiceTask 挂载位置 |
|---|---|---|
| `NODE_COMPLETED` | 普通用户任务完成 | 不需要挂 ServiceTask |
| `REJECT_OCCURRED` | 用户任务驳回 | 不需要挂 ServiceTask |

**节点回调 Payload（流程中心 → 业务系统）**：

```json
{
  "businessId": "SEMI_AUTO_001",
  "processInstanceId": "proc-uuid-001",
  "processDefinitionKey": "personnel_selection_approval",
  "businessType": "personnel_selection_approval",
  "callbackType": "NODE_COMPLETED",
  "taskDefinitionKey": "ut02_inspection_office_review",
  "nodeSemantic": "INSPECTION_OFFICE_REVIEW",
  "rejectTargetNodeKey": null,
  "lastAuditRecord": {
    "action": "approve",
    "operatorId": "EMP_005",
    "comment": "审核通过",
    "rejectReason": null,
    "operatedAt": "2024-01-15T09:30:00Z",
    "slotSelections": [
      { "slotKey": "integrity_dept_reviewer", "label": "纪检部审核人", "users": ["EMP_010"] }
    ]
  },
  "triggeredAt": "2024-01-15T09:30:01Z"
}
```

**callbackUrl 解析规则**：
```
1. slotConfig 中节点的 callbackUrl 为有效 URL → 发送节点级回调
2. slotConfig 中节点显式声明 callbackUrl 为 null/空 → 跳过，返回 200，不降级
3. slotConfig 中节点未声明 callbackUrl → 启动时 callback.url 流程级兼容降级
4. 未声明节点 callbackUrl 且流程级 callback.url 为空 → 跳过，返回 200
```

**错误场景**：

| 场景 | 预期 |
|---|---|
| 节点显式声明 `callbackUrl` 为 null/空 | 跳过节点回调，不降级，主流程继续 |
| 节点回调非 2xx 或异常 | 记录 Error，不阻塞已完成的 Flowable 任务 |
| ES 元数据不存在 | 500（触发 Flowable 重试） |
| 流程结束通知失败（非 2xx） | 500（触发 Flowable 重试） |

---

## 10. 端到端完整测试流程

### 10.1 半自动流程全链路

```
1.  POST /api/flowable/bpmn/deploy（部署 BPMN + slotConfig）
2.  POST /api/processes/start（businessId=E2E_SEMI_001，场景 2.1）
3.  GET  /api/processes/E2E_SEMI_001/progress（确认首节点 + 推荐人）
4.  GET  /api/processes/E2E_SEMI_001/flow-render（确认流程图）
5.  GET  /api/tasks/pending?employeeId=EMP_START（确认首节点待办）
6.  POST /api/tasks/complete（EMP_START 完成首节点，传 NextSlotSelections）
7.  GET  /api/processes/E2E_SEMI_001/progress（确认推进 + 推荐人更新）
8.  重复步骤 5-7，按各节点 assignee 逐步完成
9.  GET  /api/processes/E2E_SEMI_001/status → status=completed
10. GET  /api/processes/E2E_SEMI_001/audit-history → 所有节点 approve 记录
```

### 10.2 全自动流程全链路

```
1.  部署 slotConfig（含 restrictToRecommended=true 的节点）
2.  POST /api/processes/start（businessId=E2E_FULL_001，场景 2.3）
3.  GET  /progress → slotRecommendedUsers[slotKey] 有值，restrictToRecommended[slotKey]=true
4.  前端按 requiredSlots 渲染，按 slotKey 确认人选 → 提交 NextSlotSelections
5.  重复完成直到 status=completed
6.  验证审计记录 hasOutOfRecommendedRange=false
```

### 10.3 驳回链路

```
1.  启动流程（businessId=E2E_REJECT_001）
2.  完成首节点
3.  第二节点驳回（action=2，rejectCode=TO_STARTER）
4.  GET /progress → currentNodes 回到首节点
5.  GET /flow-render → completedRecords 含 outcome=rejected_return
6.  验证业务系统收到 REJECT_OCCURRED 回调
7.  首节点重新完成，流程继续推进
```

### 10.4 转派链路

```
1.  启动流程（businessId=E2E_REASSIGN_001）
2.  完成首节点，流程到第二节点（assignee=EMP_001）
3.  POST /api/tasks/reassign（EMP_001 → EMP_006）
4.  GET /tasks/pending?employeeId=EMP_006 → 出现任务
5.  GET /tasks/pending?employeeId=EMP_001 → 任务消失
6.  GET /flow-render → completedRecords 含 outcome=reassigned
7.  EMP_006 完成该节点，流程继续
```

---

## 11. BPMN HTTP ServiceTask 配置模板

普通节点完成回调不再需要配置 HTTP ServiceTask。业务系统 URL 由流程中心从 slotConfig 的节点 `callbackUrl` 读取，并在用户任务完成后主动调用。

BPMN 中只保留最后一个流程完成契约 HTTP ServiceTask，用于调用 `/api/callback/flowable`：

```xml
<serviceTask id="st02_framework_callback"
             name="通知流程中心流程已完成"
             flowable:type="http">
  <extensionElements>
    <flowable:field name="requestMethod"><flowable:string>POST</flowable:string></flowable:field>
    <flowable:field name="requestUrl"><flowable:expression>${frameworkCallbackUrl}</flowable:expression></flowable:field>
    <flowable:field name="requestHeaders"><flowable:string>Content-Type: application/json</flowable:string></flowable:field>
    <flowable:field name="requestBody">
      <flowable:expression>{"processInstanceId":"${execution.processInstanceId}","businessId":"${businessId}","processDefinitionKey":"${processDefinitionKey}"}</flowable:expression>
    </flowable:field>
  </extensionElements>
</serviceTask>
```

---

## 12. 接口速查表

| 接口 | 方法 | 路径 |
|---|---|---|
| 部署 BPMN | POST | `/api/flowable/bpmn/deploy` |
| 查询节点配置 | GET | `/api/flowable/bpmn/{key}/nodes` |
| 启动流程 | POST | `/api/processes/start` |
| 终止流程 | POST | `/api/processes/terminate` |
| 查询待办 | GET | `/api/tasks/pending` |
| 完成任务 | POST | `/api/tasks/complete` |
| 转派任务 | POST | `/api/tasks/reassign` |
| 流程进度 | GET | `/api/processes/{businessId}/progress` |
| 流程图渲染 | GET | `/api/processes/{businessId}/flow-render` |
| 审批历史 | GET | `/api/processes/{businessId}/audit-history` |
| 流程状态 | GET | `/api/processes/{businessId}/status` |
| 流程列表 | GET | `/api/processes` |
| Flowable 回调 | POST | `/api/callback/flowable` |
---

## 当前有效说明：slotConfigJson / slots 配置

> 本节是当前代码和实际 API 验证后的准则。下方历史章节里如有描述冲突，以本节为准。

### 认证与启动变量

当前接口使用 `Authorization: Bearer {jwt}` 解析当前用户，JWT 中按 `Jwt:UseridClaim` 读取用户工号，默认 claim 为 `userid`。旧文档中的 `X-User-Id` 只作为历史说明，不是当前认证入口。

启动流程时，`businessType` 必须能映射到 Flowable 的 `processDefinitionKey`。映射来自 `appsettings.json`：

```json
{
  "BusinessTypeProcessMapping": {
    "Mappings": {
      "problem_zero": "problem_zero"
    }
  }
}
```

问题归零 BPMN 首节点使用 `${starterAssignee}`，当前代码不会自动注入该变量，所以启动请求需要在 `businessVariables` 中传入：

```json
{
  "businessType": "problem_zero",
  "businessId": "PZ_001",
  "initialSlotSelections": [
    { "slotKey": "team_leader", "users": ["EMP_TL1"] }
  ],
  "businessVariables": {
    "starterAssignee": "EMP_STARTER"
  }
}
```

### 节点配置字段

`slotConfigJson` 是随 BPMN 部署提交的节点配置数组。数组中每个对象对应一个 BPMN `userTask`。

```json
{
  "taskDefinitionKey": "ut03_quality_group_confirm",
  "nodeSemantic": "PROBLEM_ZERO_QUALITY_GROUP_CONFIRM",
  "pageCode": "ProblemZero/QualityGroupConfirmForm",
  "roleKey": "problem_zero_quality_member",
  "assigneeMode": "multiple",
  "canReassign": true,
  "canReject": true,
  "rejectOptions": [
    {
      "rejectCode": "TO_STARTER",
      "label": "退回发起人修改",
      "description": "质控确认不通过，退回问题发起人修改"
    }
  ],
  "isRejectTarget": false,
  "rejectCode": null,
  "isStarterNode": false,
  "isConvergencePoint": false,
  "slots": []
}
```

| 字段 | 含义 |
|---|---|
| `taskDefinitionKey` | 必须等于 BPMN 中的 `userTask id` |
| `nodeSemantic` | 当前节点业务语义，供前端路由表单和业务系统识别 |
| `pageCode` | 前端页面/组件编码；如果是 http/https URL，待办接口会拼 `pageUrl` |
| `roleKey` | 当前节点处理角色，即“谁处理当前节点” |
| `assigneeMode` | 当前节点处理模式，`single` 或 `multiple`，用于前端/语义展示 |
| `canReassign` | 当前节点是否允许转派；默认 `false` |
| `canReject` | 当前节点是否允许驳回 |
| `rejectOptions` | 当前节点可驳回到哪些目标，提交驳回时使用其中的 `rejectCode` |
| `isRejectTarget` | 当前节点是否能作为驳回落点 |
| `rejectCode` | 当前节点作为驳回落点时的代码 |
| `isStarterNode` | 是否发起节点 |
| `isConvergencePoint` | 是否汇聚/不可撤回节点 |
| `callbackUrl` | 节点完成后的业务回调地址；空/null 表示禁用节点级回调 |
| `slots` | 当前节点完成时，前端需要渲染并提交的路径/选人槽 |

### slots 的真实职责

`slots` 描述的是“当前节点完成时，用户需要为后续路径提交什么”。它既可以是下一人工节点的选人槽，也可以是前端用于展示路径选择的 no-op 槽。

| 字段 | 含义 |
|---|---|
| `slotKey` | 前端提交 `nextSlotSelections` / `initialSlotSelections` 时使用的唯一 key |
| `roleKey` | 该槽位候选人来自哪个推荐池，即 `RecommendedAssigneesSnapshot[slot.roleKey]` |
| `label` | 前端展示文案 |
| `mode` | `single` 或 `multiple`；转换为变量时决定写 string 还是 string list |
| `variableName` | 最终写入 Flowable 的流程变量名 |
| `required` | 当 `conditionalOn` 满足且该槽位参与转换时，是否必须提交 users |
| `conditionalOn` | 条件表达式，如 `IS_SOLVED==false`；满足时该槽位才生效 |
| `restrictToRecommended` | 是否建议前端限制在推荐人范围内；后端只做越界审计，不强拦截 |

三键不要混用：

| 字段 | 不能替代谁 | 原因 |
|---|---|---|
| `slotKey` | 不能替代 `variableName` | 它只是前端提交 key，不写入 Flowable |
| `roleKey` | 不能替代 `slotKey` | 它只是推荐人池 key，不是提交 key |
| `variableName` | 不能替代 `slotKey` | 它只给 Flowable 用，前端不应按它提交 |

通常，选“下一个人工节点处理人”时，`slots[].roleKey` 应该与下一个节点外层 `roleKey` 一致，因为它们指向同一个业务角色推荐池。但这不是靠命名推导，而是配置明确声明。

### conditionalOn 的行为

`conditionalOn` 由完成任务时的 `businessVariables` 触发。当前支持：

```text
IS_SOLVED==true
IS_SOLVED==false
PROBLEM_ATTRIBUTE=true
!SOME_FLAG=true
SOME_VALUE=abc
```

运行时转换规则：

1. 条件不满足的 slot 会被跳过。
2. 被跳过的 slot 不校验 `required`，也不写入 `variableName`。
3. 条件满足且 `required=true` 时，必须提交非空 `users`。
4. `nextSlotSelections` 里提交未知 `slotKey` 会报错。

注意：当前 `/api/tasks/pending` 会返回节点配置里的全部 `requiredSlots`，不会根据尚未提交的 `businessVariables` 动态过滤条件槽。前端需要用页面上的业务选项，例如“是否已解决”，配合 `conditionalOn` 自己决定当前显示哪个槽。

### 直接结束路径怎么写

从 Flowable 执行角度，直接结束路径不需要下一人工节点，也不需要写 assignee 变量。真正驱动路径的是 BPMN 网关条件变量，例如：

```json
{
  "businessVariables": {
    "IS_SOLVED": true
  },
  "nextSlotSelections": []
}
```

如果前端需要依赖 `slots` 数量和 `conditionalOn` 渲染“true/false 两个选项”，可以配置一个 no-op 路径槽。它用于前端展示，不负责选人：

```json
{
  "slotKey": "direct_end_when_solved",
  "roleKey": "problem_zero_quality_member",
  "label": "已解决，直接结束",
  "mode": "single",
  "variableName": "__noopDirectEnd",
  "required": false,
  "conditionalOn": "IS_SOLVED==true",
  "restrictToRecommended": false
}
```

用户选择 true 时可以提交：

```json
{
  "businessId": "PZ_001",
  "employeeId": "EMP_Q1",
  "action": 1,
  "businessVariables": {
    "IS_SOLVED": true
  },
  "nextSlotSelections": [
    { "slotKey": "direct_end_when_solved", "users": [] }
  ]
}
```

因为 `required=false` 且 `users=[]`，转换器不会写入 `__noopDirectEnd`。流程仍由 `IS_SOLVED=true` 走到 BPMN 的结束路径。

### 问题归零 UT-03 推荐配置

```json
{
  "taskDefinitionKey": "ut03_quality_group_confirm",
  "roleKey": "problem_zero_quality_member",
  "assigneeMode": "multiple",
  "slots": [
    {
      "slotKey": "direct_end_when_solved",
      "roleKey": "problem_zero_quality_member",
      "label": "已解决，直接结束",
      "mode": "single",
      "variableName": "__noopDirectEnd",
      "required": false,
      "conditionalOn": "IS_SOLVED==true",
      "restrictToRecommended": false
    },
    {
      "slotKey": "responsible_person",
      "roleKey": "problem_zero_responsible_person",
      "label": "责任人",
      "mode": "multiple",
      "variableName": "responsibleAssigneeList",
      "required": true,
      "conditionalOn": "IS_SOLVED==false",
      "restrictToRecommended": true
    }
  ]
}
```

用户选择 false 时：

```json
{
  "businessVariables": {
    "IS_SOLVED": false
  },
  "nextSlotSelections": [
    { "slotKey": "responsible_person", "users": ["EMP_R1"] }
  ]
}
```

### 问题归零 UT-04 条件路径

专项工作：

```json
{
  "businessVariables": {
    "PROBLEM_ATTRIBUTE": true
  },
  "nextSlotSelections": [
    { "slotKey": "counterpart_leader", "users": ["EMP_C1"] }
  ]
}
```

非专项工作：

```json
{
  "businessVariables": {
    "PROBLEM_ATTRIBUTE": false
  },
  "nextSlotSelections": [
    { "slotKey": "discoverer_direct", "users": ["EMP_D1"] }
  ]
}
```

### 多实例、会签、或签

BPMN 中的 `multiInstanceLoopCharacteristics` 只表示多实例任务，不天然等于会签或或签。

```xml
<multiInstanceLoopCharacteristics isSequential="false"
                                  flowable:collection="${teamLeaderAssigneeList}"
                                  flowable:elementVariable="teamLeaderAssignee">
  <completionCondition>${nrOfCompletedInstances &gt;= 1}</completionCondition>
</multiInstanceLoopCharacteristics>
```

| 配置 | 含义 |
|---|---|
| `isSequential="false"` | 并行创建多个人的任务 |
| `flowable:collection` | 人员列表变量 |
| `flowable:elementVariable` | 当前实例中的单个人变量 |
| `completionCondition >= 1` | 任意一人完成后，整个多实例节点结束 |

所以当前问题归零这些节点是“并行或签”。如果要会签，通常使用：

```xml
<completionCondition>${nrOfCompletedInstances == nrOfInstances}</completionCondition>
```

或者不配置 `completionCondition`，让 Flowable 默认等待所有多实例完成。
