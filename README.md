# 流程中心 API 测试规范文档

> **版本**：Patch Plan V1.2  
> **基准系统**：FlowableWrapper (.NET 8) + Flowable 7.2 + Elasticsearch + Redis  
> **文档用途**：交付测试 Agent 进行全方位接口测试  
> **统一响应格式**：所有接口返回 `ApiResult<T>` 包装，HTTP 状态码统一 200，通过 `success` 字段区分业务成功与失败

---

## 0. 公共约定

### 0.1 请求头

| Header | 说明 | 必填 |
|---|---|---|
| `Content-Type` | `application/json` | 是（POST 请求） |
| `X-User-Id` | 当前操作人工号，未传时部分接口读此头 | 视接口 |

### 0.2 统一响应结构

```json
{
  "success": true,
  "message": "操作成功",
  "data": { }
}
```

错误响应（HTTP 200，`success: false`）：

```json
{
  "success": false,
  "message": "错误描述",
  "errorCode": "ERROR_CODE"
}
```

### 0.3 常用错误码

| 错误码 | 含义 |
|---|---|
| `FLOWABLE_START_FAILED` | Flowable 启动失败 |
| `PROCESS_METADATA_INDEX_ORPHAN` | 流程已启动但 ES 写入两次均失败 |
| `REJECT_CODE_REQUIRED` | 驳回时未传 rejectCode |
| `REJECT_NOT_ALLOWED` | 当前节点不允许驳回 |
| `REJECT_TARGET_NOT_FOUND` | 未找到 rejectCode 对应的目标节点 |
| `ASSIGNEE_MISSING` | 必选节点无人可分配 |

---

## 1. 流程生命周期

### 1.1 启动流程

**POST** `/api/processes/start`

#### 场景 A：全自动流程（AssigneeContract，Phase 1 新增）

启动时一次性按角色传入全流程处理人，由 Flowable 自动绑定各节点 assignee。

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "SELECTION_2024_001",
  "assigneeContract": {
    "roles": [
      { "roleKey": "dept_head",                    "mode": "single",   "users": ["EMP_001"] },
      { "roleKey": "inspection_office_reviewer",   "mode": "single",   "users": ["EMP_005"] },
      { "roleKey": "final_approver",               "mode": "single",   "users": ["EMP_010"] }
    ]
  },
  "businessVariables": { "batchName": "2024年第一批" },
  "callback": {
    "url": "https://biz-system.internal/api/process-callback",
    "timeoutSeconds": 30
  }
}
```

#### 场景 B：全自动流程 + 多实例会签节点（LoopAssignments，Phase 1 新增）

```json
{
  "businessType": "joint_review",
  "businessId": "REVIEW_2024_001",
  "assigneeContract": {
    "roles": [
      { "roleKey": "dept_head", "mode": "single", "users": ["EMP_001"] }
    ]
  },
  "loopAssignments": {
    "items": [
      {
        "loopKey": "joint_review_loop",
        "businessKey": "DEPT_A",
        "roles": [
          {
            "roleKey": "reviewer",
            "mode": "multiple",
            "users": ["EMP_010", "EMP_011", "EMP_012"]
          }
        ]
      }
    ]
  },
  "callback": { "url": "https://biz-system.internal/api/process-callback" }
}
```

#### 场景 C：半自动流程（推荐处理人，Phase 1 新增）

启动时传入推荐人，前端读取后初始化选人区，每步由用户确认后提交 `NextSlotSelections`。

```json
{
  "businessType": "daily_supervision",
  "businessId": "SUPER_2024_001",
  "recommendedAssignees": {
    "dept_head_slot":                    ["EMP_001", "EMP_002"],
    "inspection_office_reviewer_slot":   ["EMP_005"],
    "final_approver_slot":               ["EMP_010"]
  },
  "businessVariables": { "needFeedback": true },
  "callback": { "url": "https://biz-system.internal/api/process-callback" }
}
```

#### 场景 D：旧流程（InitialSlotSelections，兼容路径，行为不变）

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "SELECTION_2024_002",
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_001"] }
  ],
  "businessVariables": {},
  "callback": { "url": "https://biz-system.internal/api/process-callback" }
}
```

#### 场景 E：AssigneeContract 与 InitialSlotSelections 同时传入（AssigneeContract 优先）

```json
{
  "businessType": "personnel_selection_approval",
  "businessId": "SELECTION_2024_003",
  "assigneeContract": {
    "roles": [{ "roleKey": "dept_head", "mode": "single", "users": ["EMP_001"] }]
  },
  "initialSlotSelections": [
    { "slotKey": "group_leader", "users": ["EMP_999"] }
  ]
}
```

> **预期**：`AssigneeContract` 生效，`InitialSlotSelections` 被忽略，服务端日志出现 Warning

#### 成功响应

```json
{
  "success": true,
  "data": {
    "processInstanceId": "proc-uuid-001",
    "businessId": "SELECTION_2024_001",
    "firstTaskId": "task-uuid-001",
    "firstNodeSemantic": "DEPT_HEAD_HANDLE",
    "firstPageCode": "SelectionCollection/DeptHeadHandleForm"
  }
}
```

#### 错误场景

| 场景 | 预期错误码 | 预期 HTTP |
|---|---|---|
| `businessId` 重复启动 | 业务错误，已存在运行中流程 | 200 `success:false` |
| `businessType` 未配置映射 | 业务错误 | 200 `success:false` |
| Flowable 不可用 | `FLOWABLE_START_FAILED` | 200 `success:false` |
| ES 写入失败（两次均失败） | `PROCESS_METADATA_INDEX_ORPHAN` | 200 `success:false` |

---

### 1.2 终止流程

**POST** `/api/processes/terminate`

```json
{
  "businessId": "SELECTION_2024_001",
  "reason": "业务取消，管理员手动终止"
}
```

**成功响应**：`{ "success": true, "message": "流程已终止" }`

**错误场景**：`businessId` 不存在或已终止 → `success: false`

---

## 2. 任务执行

### 2.1 完成任务（审批通过）

**POST** `/api/tasks/complete`

#### 场景 A：全自动流程通过（无需传 NextSlotSelections，处理人已在启动时注入）

```json
{
  "businessId": "SELECTION_2024_001",
  "action": 1,
  "comment": "同意，资料齐全"
}
```

#### 场景 B：半自动流程通过（前端传 NextSlotSelections，唯一最终生效的人员来源）

```json
{
  "businessId": "SUPER_2024_001",
  "action": 1,
  "comment": "审核通过",
  "nextSlotSelections": [
    { "slotKey": "inspection_office_reviewer_slot", "users": ["EMP_005"] }
  ]
}
```

#### 场景 C：传入推荐范围外人员（RestrictToRecommended=true 的 slot）

```json
{
  "businessId": "SUPER_2024_001",
  "action": 1,
  "nextSlotSelections": [
    { "slotKey": "inspection_office_reviewer_slot", "users": ["EMP_999"] }
  ]
}
```

> **预期**：流程正常推进（不拦截），审计记录 `hasOutOfRecommendedRange = true`，日志出现 `[RECOMMEND_RANGE_EXCEEDED]`

#### 场景 D：并行节点，必须传 taskId

```json
{
  "businessId": "REVIEW_2024_001",
  "taskId": "task-uuid-002",
  "action": 1
}
```

#### 场景 E：附带业务变量（网关条件）

```json
{
  "businessId": "SELECTION_2024_001",
  "action": 1,
  "businessVariables": { "needFeedback": true }
}
```

**成功响应**：`{ "success": true, "message": "审批通过" }`

---

### 2.2 完成任务（驳回）

**POST** `/api/tasks/complete`

```json
{
  "businessId": "SELECTION_2024_001",
  "action": 2,
  "rejectCode": "TO_DEPT_HEAD",
  "rejectReason": "材料不完整，退回部门一把手重新填写"
}
```

**错误场景**：

| 场景 | 预期错误码 |
|---|---|
| `rejectCode` 未传 | `REJECT_CODE_REQUIRED` |
| `rejectReason` 未传 | `REJECT_REASON_REQUIRED` |
| 当前节点 `CanReject=false` | `REJECT_NOT_ALLOWED` |
| `rejectCode` 不在当前节点 `RejectOptions` 中 | `REJECT_CODE_INVALID` |

---

### 2.3 查询待办任务

**GET** `/api/tasks/pending?employeeId=EMP_001&pageIndex=1&pageSize=20`

可选参数：`businessType`（过滤业务类型）

**响应结构**：

```json
{
  "success": true,
  "data": {
    "items": [
      {
        "taskId": "task-uuid-001",
        "taskName": "部门一把手处理",
        "businessId": "SELECTION_2024_001",
        "businessType": "personnel_selection_approval",
        "nodeSemantic": "DEPT_HEAD_HANDLE",
        "pageCode": "SelectionCollection/DeptHeadHandleForm",
        "canReject": true,
        "rejectOptions": [
          { "rejectCode": "TO_STARTER", "label": "退回发起人" }
        ],
        "requiredSlots": [
          {
            "slotKey": "inspection_office_reviewer_slot",
            "label": "审核人",
            "variableName": "inspectionOfficeReviewerAssignee",
            "mode": "single",
            "required": true,
            "restrictToRecommended": false
          }
        ],
        "createTime": "2024-01-15T08:30:00Z"
      }
    ],
    "total": 1,
    "pageIndex": 1,
    "pageSize": 20
  }
}
```

---

### 2.4 转派任务

**POST** `/api/tasks/reassign`

```json
{
  "businessId": "SELECTION_2024_001",
  "taskId": "task-uuid-001",
  "newAssignees": ["EMP_002"],
  "reason": "原处理人请假"
}
```

> 转派只作用于当前节点当前 Task，不影响未来节点的预配置处理人。

---

## 3. 流程查询

### 3.1 查询流程进度（含推荐人，Phase 1 新增字段）

**GET** `/api/processes/{businessId}/progress`

**响应结构**（重点标注 Phase 1 新增字段）：

```json
{
  "success": true,
  "data": {
    "businessId": "SUPER_2024_001",
    "processInstanceId": "proc-uuid-001",
    "processDefinitionKey": "daily_supervision",
    "businessType": "daily_supervision",
    "status": "running",
    "createdBy": "EMP_100",
    "createdTime": "2024-01-15T08:00:00Z",
    "completedTime": null,
    "currentNodes": [
      {
        "taskId": "task-uuid-001",
        "nodeId": "ut01_dept_head_handle",
        "nodeName": "部门一把手处理",
        "nodeSemantic": "DEPT_HEAD_HANDLE",
        "pageCode": "SelectionCollection/DeptHeadHandleForm",
        "assignee": "EMP_001",
        "candidateUsers": [],
        "createTime": "2024-01-15T08:01:00Z",
        "recommendedUsers": {
          "dept_head_slot": ["EMP_001", "EMP_002"]
        },
        "restrictToRecommended": {
          "dept_head_slot": false
        }
      }
    ],
    "auditHistory": [
      {
        "taskDefinitionKey": "ut00_starter",
        "nodeSemantic": "STARTER",
        "pageCode": "SelectionCollection/StarterForm",
        "action": "approve",
        "operatorId": "EMP_100",
        "comment": "提交申请",
        "operatedAt": "2024-01-15T08:00:30Z",
        "slotSelections": []
      }
    ]
  }
}
```

**Phase 1 新增字段测试要点**：

| 字段 | 路径 | 测试内容 |
|---|---|---|
| `recommendedUsers` | `currentNodes[].recommendedUsers` | Key=slotKey，Value=推荐人列表；无推荐人时为空字典 `{}` |
| `restrictToRecommended` | `currentNodes[].restrictToRecommended` | Key=slotKey，Value=bool；无论 true/false 均输出 |

**场景矩阵**：

| 场景 | `recommendedUsers` 预期 | `restrictToRecommended` 预期 |
|---|---|---|
| 半自动流程，启动时传了推荐人 | 当前节点对应 slot 有值 | 按 slotConfig 配置输出 |
| 全自动流程，启动时未传推荐人 | `{}` | `{}` |
| 旧流程 | `{}` | `{}` |
| 流程已结束，`currentNodes` 为空 | 不存在 | 不存在 |

---

### 3.2 查询审批历史（含越界审计，Phase 1 新增字段）

**GET** `/api/processes/{businessId}/audit-history`

**响应结构**（重点标注 Phase 1 新增审计字段）：

```json
{
  "success": true,
  "data": [
    {
      "taskDefinitionKey": "ut01_dept_head_handle",
      "nodeSemantic": "DEPT_HEAD_HANDLE",
      "pageCode": "SelectionCollection/DeptHeadHandleForm",
      "action": "approve",
      "operatorId": "EMP_001",
      "comment": "同意",
      "rejectReason": null,
      "operatedAt": "2024-01-15T09:00:00Z",
      "slotSelections": [
        {
          "slotKey": "inspection_office_reviewer_slot",
          "label": "审核人",
          "users": ["EMP_005"]
        }
      ]
    }
  ]
}
```

> **注意**：`hasOutOfRecommendedRange` 等审计越界字段存储在 ES `ProcessAuditRecord` 中，当前 `AuditRecordDto` 未对外暴露这三个字段。测试时通过 ES 直接查询 `ProcessAuditRecord` 索引验证写入。

**ES 直接验证**（审计越界字段）：

```
GET /process_audit_records/_search
{
  "query": { "term": { "businessId": "SUPER_2024_001" } }
}
```

预期字段：

```json
{
  "hasOutOfRecommendedRange": true,
  "recommendedUsersSnapshot": { "inspection_office_reviewer_slot": ["EMP_005"] },
  "restrictToRecommendedSnapshot": { "inspection_office_reviewer_slot": true }
}
```

---

### 3.3 查询流程状态（轻量）

**GET** `/api/processes/{businessId}/status`

```json
{
  "success": true,
  "data": {
    "processInstanceId": "proc-uuid-001",
    "businessId": "SELECTION_2024_001",
    "businessType": "personnel_selection_approval",
    "processDefinitionKey": "personnel_selection_approval",
    "status": "running",
    "createdBy": "EMP_100",
    "createdTime": "2024-01-15T08:00:00Z",
    "completedTime": null
  }
}
```

**status 枚举值**：`running` / `completed` / `terminated` / `callback_failed`

---

### 3.4 分页查询流程列表

**GET** `/api/processes?businessType=personnel_selection_approval&status=running&pageIndex=1&pageSize=20`

可选参数：`businessId` / `businessType` / `status` / `createdBy` / `createdTimeFrom` / `createdTimeTo`

```json
{
  "success": true,
  "data": {
    "items": [ /* ProcessListItemDto 列表 */ ],
    "total": 42,
    "pageIndex": 1,
    "pageSize": 20
  }
}
```

---

## 4. 回调接口

### 4.1 流程结束回调（Flowable → 流程中心，内部接口）

**POST** `/api/callback/flowable`

> 此接口由 Flowable 引擎调用，不由业务系统或前端直接调用。

#### 场景 A：流程结束回调（原有行为，无 callbackType）

```json
{
  "processInstanceId": "proc-uuid-001",
  "businessId": "SELECTION_2024_001",
  "processDefinitionKey": "personnel_selection_approval"
}
```

**预期**：ES `status = completed`，业务系统收到流程结束通知，返回 200。

#### 场景 B：节点级 on_complete 回调（Phase 1 新增）

```json
{
  "processInstanceId": "proc-uuid-001",
  "businessId": "SELECTION_2024_001",
  "processDefinitionKey": "personnel_selection_approval",
  "variables": {
    "callbackType": "node_on_complete",
    "callbackNodeKey": "ut01_dept_head_handle",
    "nodeCallbackUrl": "https://biz-system.internal/api/node-callback"
  }
}
```

**预期**：向 `nodeCallbackUrl` 发送一次 POST，ES `status` 不变（仍为 `running`），返回 200。

**节点级回调 Payload**（流程中心 → 业务系统）：

```json
{
  "businessId": "SELECTION_2024_001",
  "processInstanceId": "proc-uuid-001",
  "taskDefinitionKey": "ut01_dept_head_handle",
  "callbackTiming": "on_complete",
  "triggeredAt": "2024-01-15T09:00:00Z"
}
```

#### 场景 C：节点回调，节点未完成（callbackNodeKey 对应节点仍活跃）

```json
{
  "processInstanceId": "proc-uuid-001",
  "businessId": "SELECTION_2024_001",
  "variables": {
    "callbackType": "node_on_complete",
    "callbackNodeKey": "ut01_dept_head_handle",
    "nodeCallbackUrl": "https://biz-system.internal/api/node-callback"
  }
}
```

**预期**：日志出现 `节点 [ut01_dept_head_handle] 尚未完成，跳过 on_complete 回调`，业务系统不收到通知，返回 200。

#### 场景 D：幂等测试（已是 completed 状态重复回调）

**预期**：直接返回 200，ES 状态不变，不重复通知业务系统。

#### 错误响应（触发 Flowable 重试）

| 场景 | 预期 HTTP |
|---|---|
| ES 元数据不存在（写入延迟） | 500（触发 Flowable 重试） |
| 业务系统通知失败（非 2xx） | 500（触发 Flowable 重试） |

---

## 5. Phase 1 专项测试矩阵

### 5.1 AssigneeContract 投影验证

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-AC-01 | 传 `assigneeContract`，角色数=3 | Flowable 变量中对应 `variableName` 正确赋值 |
| T-AC-02 | `AssigneeContract.mode=single` | 变量值为 `string`（非数组） |
| T-AC-03 | `AssigneeContract.mode=multiple` | 变量值为 `List<string>` |
| T-AC-04 | RoleKey 在 semanticMap 中无对应节点 | 静默跳过，其他节点正常投影 |
| T-AC-05 | mode 不匹配（roleKey=single，slot=multiple） | Warning 日志，该 slot 跳过，其他 slot 正常 |
| T-AC-06 | `AssigneeContract` + `InitialSlotSelections` 同传 | `AssigneeContract` 生效，日志有 Warning |

### 5.2 LoopAssignments 投影验证

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-LA-01 | 传 `loopAssignments`，1个 loopKey，3个 user | Flowable 变量 `{loopKey}_{roleKey}_list = ["EMP_010","EMP_011","EMP_012"]` |
| T-LA-02 | 同一 loopKey 出现在两个 LoopItem | Users 顺序合并为一个 List |
| T-LA-03 | `LoopKey` 为空的 item | Warning 日志，该 item 跳过 |
| T-LA-04 | `Users` 为空的 role | Warning 日志，该 role 跳过 |
| T-LA-05 | 同时传 `AssigneeContract` + `LoopAssignments` | 两者变量独立投影，无覆盖冲突 |

### 5.3 推荐处理人验证

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-RA-01 | 启动时传 `recommendedAssignees` | ES `ProcessMetadataDocument.recommendedAssigneesSnapshot` 写入正确 |
| T-RA-02 | 查询 `GET /progress` | `currentNodes[].recommendedUsers` 返回对应 slot 的推荐人 |
| T-RA-03 | `restrictToRecommended=false` + 提交推荐范围外人员 | 流程正常推进，审计记录 `hasOutOfRecommendedRange=false` |
| T-RA-04 | `restrictToRecommended=true` + 提交推荐范围外人员 | 流程正常推进（不拦截），审计记录 `hasOutOfRecommendedRange=true`，日志有 `[RECOMMEND_RANGE_EXCEEDED]` |
| T-RA-05 | `restrictToRecommended=true` + 提交范围内人员 | 流程正常推进，审计记录 `hasOutOfRecommendedRange=false` |
| T-RA-06 | 旧流程，未传 `recommendedAssignees` | `currentNodes[].recommendedUsers = {}`，`restrictToRecommended = {}`，无任何影响 |

### 5.4 ES 写入轻量补偿验证

| 测试用例 | 模拟方式 | 验证点 |
|---|---|---|
| T-ES-01 | 模拟 ES 首次写入失败，第二次成功 | 日志出现 `[ES_WRITE_RETRY_SUCCESS]`，流程正常启动 |
| T-ES-02 | 模拟 ES 两次写入均失败 | 日志出现 `[ES_WRITE_ORPHAN]`（Critical），错误码 `PROCESS_METADATA_INDEX_ORPHAN`，接口返回 `success:false` |

### 5.5 节点完成判定验证

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-NC-01 | 单任务节点已完成 | `IsNodeCompletedAsync` 返回 `true` |
| T-NC-02 | 会签节点，部分子任务仍活跃 | `IsNodeCompletedAsync` 返回 `false` |
| T-NC-03 | 会签节点，所有子任务完成 | `IsNodeCompletedAsync` 返回 `true` |
| T-NC-04 | Flowable 查询异常 | 保守返回 `false`，日志有 Warning |

### 5.6 节点级回调验证

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-CB-01 | 发送 `callbackType=node_on_complete`，节点已完成 | 业务系统收到 POST，返回 200 |
| T-CB-02 | 发送 `callbackType=node_on_complete`，节点未完成 | 业务系统不收到通知，返回 200，日志有 Warning |
| T-CB-03 | 缺少 `callbackNodeKey` | 返回 200，日志有 Warning，业务系统不收到通知 |
| T-CB-04 | 缺少 `nodeCallbackUrl` | 返回 200，日志有 Warning，业务系统不收到通知 |
| T-CB-05 | 业务系统回调返回 500 | 流程中心返回 200（不重试，Phase 1 限制），日志有 Error |
| T-CB-06 | 无 `callbackType`（原有流程结束回调） | 走原有路径，ES status=completed，行为不变 |

### 5.7 向后兼容性验证（回归）

| 测试用例 | 操作 | 验证点 |
|---|---|---|
| T-BC-01 | 旧流程，只传 `InitialSlotSelections` | 流程正常启动，变量正确投影 |
| T-BC-02 | 旧流程，`CompleteTask` 带 `NextSlotSelections` | 任务正常完成，变量正确传递 |
| T-BC-03 | 旧流程驳回 | 驳回逻辑不变，跳转正确 |
| T-BC-04 | 旧流程转派 | 转派成功，当前节点 assignee 变更 |
| T-BC-05 | 旧流程查询 progress | `recommendedUsers` 和 `restrictToRecommended` 均为空字典 `{}`，其他字段正常 |

---

## 6. BPMN 配置参考

### 6.1 节点级回调 HTTP ServiceTask 配置

在需要触发 on_complete 回调的节点完成后，在 BPMN 中追加一个 HTTP ServiceTask：

```xml
<serviceTask id="st_notify_dept_head_complete"
             name="通知部门一把手节点完成"
             flowable:type="http">
  <extensionElements>
    <flowable:field name="requestMethod" stringValue="POST"/>
    <flowable:field name="requestUrl"
                    expression="${frameworkCallbackUrl}"/>
    <flowable:field name="requestHeaders"
                    stringValue="Content-Type: application/json"/>
    <flowable:field name="requestBody"
                    expression="{
                      &quot;processInstanceId&quot;:&quot;${execution.processInstanceId}&quot;,
                      &quot;businessId&quot;:&quot;${businessId}&quot;,
                      &quot;variables&quot;:{
                        &quot;callbackType&quot;:&quot;node_on_complete&quot;,
                        &quot;callbackNodeKey&quot;:&quot;ut01_dept_head_handle&quot;,
                        &quot;nodeCallbackUrl&quot;:&quot;${nodeCallbackUrl}&quot;
                      }
                    }"/>
    <flowable:field name="responseVariableName"
                    stringValue="nodeCallbackResponse"/>
  </extensionElements>
</serviceTask>
```

### 6.2 多实例会签节点配置

```xml
<userTask id="ut_joint_review" name="联合审批"
          flowable:assignee="${currentReviewer}">
  <multiInstanceLoopCharacteristics
    isSequential="false"
    flowable:collection="${joint_review_loop_reviewer_list}"
    flowable:elementVariable="currentReviewer">
    <completionCondition>
      ${nrOfCompletedInstances == nrOfInstances}
    </completionCondition>
  </multiInstanceLoopCharacteristics>
  <extensionElements>
    <flowable:field name="roleKey"        stringValue="reviewer"/>
    <flowable:field name="assigneeMode"   stringValue="multiple"/>
    <flowable:field name="callbackTiming" stringValue="on_complete"/>
  </extensionElements>
</userTask>
```

---

## 7. 日志关键字速查

| 关键字 | 级别 | 含义 |
|---|---|---|
| `[ES_WRITE_FAIL]` | Error | ES 首次写入失败，正在重试 |
| `[ES_WRITE_RETRY_SUCCESS]` | Warning | ES 写入重试成功 |
| `[ES_WRITE_ORPHAN]` | **Critical** | ES 写入两次均失败，存在孤儿实例 |
| `PROCESS_METADATA_INDEX_ORPHAN` | **Critical** | 同上，错误码 |
| `[RECOMMEND_RANGE_EXCEEDED]` | Warning | 提交了推荐范围外人员（后端审计，不拦截） |
| `AssigneeContract 优先，InitialSlotSelections 已忽略` | Warning | 两种选人方式同时传入 |
| `节点 [X] 尚未完成，跳过 on_complete 回调` | Warning | 节点级回调提前触发 |

---

*— API 测试规范文档 · 对应 Patch Plan V1.2 · N1-N10 全节点*
