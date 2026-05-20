# 流程中心升级 Patch Plan V1.2

> **适用范围**：FlowableWrapper (.NET 8) + Flowable 7.2 + ES + Redis  
> **基准文档**：`process_center_evolution_plan.md`（演进方案报告）  
> **版本**：V1.2（在 V1.1 基础上新增推荐处理人动态注入 + 审计感知设计）  
> **修订说明**：新增 §11 推荐处理人机制；tasks.md 核实结论内嵌至 §0；T004 正式删除；T010 实现注意事项标注

---

## 0. 阅读指南

本文档是跨窗口共享的唯一实施事实来源。所有实现窗口以此文档为准。

| 章节 | 内容 | 受影响文件 |
|---|---|---|
| §1 | 前置约定与设计原则 | — |
| §2 | 人员来源模型（核心规则，必须先读） | — |
| §3 | NodeContract（节点契约基础） | `ProcessMetadataDocument.cs` |
| §4 | AssigneeContract（全流程角色选人） | `StartProcessRequest.cs` · `ProcessLifecycleAppService.cs` · 新增 `AssigneeContractConverter.cs` |
| §5 | loopAssignments（结构化多实例选人） | `StartProcessRequest.cs` · 新增 `LoopAssignmentProjector.cs` |
| §6 | 变量投影统一规则 | `ProcessLifecycleAppService.cs`（`BuildStartVariables`） |
| §7 | NextSlotSelections 兼容保留 | `TaskExecutionAppService.cs`（不改，仅标注） |
| §8 | 节点完成判定 | `ProcessQueryAppService.cs` |
| §9 | 回调机制（on_complete 极简版） | `ProcessCallbackAppService.cs` |
| §10 | ES 写入轻量补偿 | `ProcessLifecycleAppService.cs` |
| §11 | 推荐处理人机制（V1.2 新增） | `StartProcessRequest.cs` · `ProcessMetadataDocument.cs` · `ProcessLifecycleAppService.cs` · `ProcessQueryAppService.cs` · `TaskExecutionAppService.cs` · `BpmnDeploymentDto.cs` |
| §12 | BPMN 适配指导 | 各 `.bpmn` 文件 |
| §13 | 已知限制与一致性空洞 | — |
| §14 | 实施检查清单 | — |

> **禁止项**（整个 Patch 范围内一律不实现）：Outbox Pattern、each_instance 回调、NodeOverride、iframe + postMessage、NodeAction / CallbackSpec 完整系统、Saga / 补偿、Snapshot Upcoming / CallbackStatus、自动推断下一节点 fallback 补人、后端强拦截推荐范围外人员。

### tasks.md 核实结论（已入档）

| 任务 | 状态 | 处置 |
|---|---|---|
| T004 enum 定义 | ❌ 删除 | 与 Patch Plan `string` 类型定义冲突，违反 CLAUDE.md Simplicity First，整条删除不执行 |
| T010 `Convert` 调用 | ⚠️ 注意 | 调用 `SlotVariableConverter.Convert` 时须传第三个参数 `businessVariables`（取自 `StartProcessRequest.BusinessVariables`），签名：`Convert(selections, slotDefs, businessVariables)` |
| T033 注入方向 | ✅ 可执行 | `ProcessQueryAppService` 注入到 `ProcessCallbackAppService`，无循环依赖风险 |
| 其余 T001–T041 | ✅ 通过 | 与代码结构及 Patch Plan 完全对齐 |

---

## 1. 前置约定与设计原则

### 1.1 六条设计原则（不可违反）

| 原则 | 约束 | 违反后果 |
|---|---|---|
| Flowable 真相源 | 所有流程状态判定（含节点完成）必须查询 Flowable，禁止本地推断 | 状态与 Flowable 分叉 |
| 向后兼容 | `NextSlotSelections` 接口保留，现有 BPMN 不强制修改 | 已接入业务回归失败 |
| 最小闭环优先 | 可运行 > 完美架构；不预实现 Phase 2 能力 | 引入不稳定抽象 |
| 模型先行 | NodeContract / loopAssignments 只建数据模型，不建完整运行时 | 过早锁定运行时行为 |
| 不干涉引擎 | 本框架是 Flowable 的薄封装，不建第二套状态机 | 双状态机撕裂 |
| 四条人员原则 | slotConfig = 交互结构真相；recommendedAssignees = 实例推荐来源；NextSlotSelections = 唯一最终生效来源；流程中心只做转换、必填校验和审计 | 人员逻辑失控 |

### 1.2 命名约定

- **roleKey**：业务角色标识，跨节点唯一，如 `inspection_office_reviewer`
- **slotKey**：节点内选人槽位标识，仅限内部映射，不出现在 API 层
- **variableName**：Flowable 流程变量名，如 `inspectionOfficeAssignee`
- **loopKey**：多实例循环标识，如 `dept_review_loop`
- **recommendedUsers**：实例级推荐处理人，前端初始化用，非执行约束

### 1.3 现有代码保留点（不得修改）

- `SlotVariableConverter.Convert` — 签名不变，单节点 Slot 转换逻辑完全复用
- `ElasticSearchSlotConfigProvider` — 不改
- `TaskExecutionAppService.BuildCompletionVariablesAsync` — 只追加审计感知逻辑，不改现有转换路径
- 所有 `IFlowable*` 接口及实现 — 禁止修改

---

## 2. 人员来源模型（核心规则）

> **本节是整个 Patch 的设计基础，所有实现窗口必须对齐。**

### 2.1 四条人员原则

```
slotConfig（部署时）      → 交互结构真相：slot 定义、前端渲染地址、后端回调地址、restrictToRecommended
StartProcessRequest       → 实例推荐来源：recommendedAssignees（动态注入，非执行约束）
NextSlotSelections        → 唯一最终生效来源：用户确认后提交，流程中心不判断人员来源
流程中心                  → 只做：Slot 必填校验 + 变量转换 + 审计记录
```

### 2.2 新流程人员注入路径

```
启动时 → AssigneeContract（全自动流程：按角色一次性注入）
启动时 → loopAssignments（多实例节点集合变量注入）
启动时 → recommendedAssignees（半自动流程：推荐人快照，存入实例文档）

运行时 → 前端读取当前节点 recommendedUsers，初始化选人区
       → 用户确认后组装 NextSlotSelections 提交
       → 流程中心校验必填规则，转换为 Flowable 变量，写审计记录
```

### 2.3 运行中人员变更

唯一允许的运行中人员变更方式是转派（Reassign）：只作用于当前节点当前 Task，不改变未来节点配置。

### 2.4 旧流程兼容

`NextSlotSelections` 和 `InitialSlotSelections` 保留，不加 `[Obsolete]`。旧流程不使用 `AssigneeContract` 和 `recommendedAssignees`，两条路径并存互不干涉。

---

## 3. NodeContract（节点契约基础）

### 3.1 NodeSemanticInfo 新增字段

**文件**：`ProcessMetadataDocument.cs`，追加到 `NodeSemanticInfo` 末尾，原有字段不动。

```csharp
/// <summary>
/// 该节点绑定的业务角色 Key（来自 AssigneeContract）
/// 部署 BPMN 时从 extensionElements 读取写入
/// </summary>
public string RoleKey { get; set; }

/// <summary>
/// 处理人模式：single = 单人，multiple = 多人
/// </summary>
public string AssigneeMode { get; set; }

/// <summary>
/// 节点完成后触发回调时机
/// Phase 1 只支持 "on_complete"，null 表示不触发
/// </summary>
public string CallbackTiming { get; set; }
```

> `PageCode`、`IsConvergencePoint`、`CanReject`、`IsRejectTarget`、`RejectCode` 已存在，不改动。

### 3.2 NodeContract 与 NodeSemanticInfo 的关系

`NodeSemanticInfo` 即为 Phase 1 的 NodeContract 实体，不另建新类。

| NodeContract 字段 | NodeSemanticInfo 字段 | 状态 |
|---|---|---|
| `taskDefinitionKey` | `TaskDefinitionKey` | 已存在 |
| `nodeSemantic` | `NodeSemantic` | 已存在 |
| `roleKey` | `RoleKey`（新增） | §3.1 |
| `assigneeMode` | `AssigneeMode`（新增） | §3.1 |
| `callbackTiming` | `CallbackTiming`（新增） | §3.1 |
| `canReject` / `isRejectTarget` / `rejectCode` | 已有字段 | 已存在 |
| `isConvergencePoint` | `IsConvergencePoint` | 已存在 |
| `pageCode`（不驱动 iframe） | `PageCode` | 已存在 |

### 3.3 BPMN 部署时写入

**`BpmnDeploymentAppService` 新增读取**（`ParseNodeSemantics` 中，现有 `ParseFlowableFields` 已支持通用解析）：

```csharp
nodeInfo.RoleKey        = ReadField(fields, "roleKey");
nodeInfo.AssigneeMode   = ReadField(fields, "assigneeMode");
nodeInfo.CallbackTiming = ReadField(fields, "callbackTiming");
```

**BPMN extensionElements 示例**：

```xml
<flowable:field name="roleKey"        stringValue="inspection_office_reviewer"/>
<flowable:field name="assigneeMode"   stringValue="single"/>
<flowable:field name="callbackTiming" stringValue="on_complete"/>
```

---

## 4. AssigneeContract（全流程角色选人）

### 4.1 数据模型

**文件**：`StartProcessRequest.cs`（追加，不删除现有字段）

```csharp
public class AssigneeContract
{
    public List<RoleAssignment> Roles { get; set; } = new();
}

public class RoleAssignment
{
    /// <summary>业务角色 Key，对应 NodeSemanticInfo.RoleKey</summary>
    public string RoleKey { get; set; }

    /// <summary>single / multiple</summary>
    public string Mode { get; set; }

    public List<string> Users { get; set; } = new();
}
```

**`StartProcessRequest` 追加**：

```csharp
/// <summary>
/// 全流程角色选人契约（全自动流程使用）
/// 与 InitialSlotSelections 互斥，同时传入时 AssigneeContract 优先
/// </summary>
public AssigneeContract AssigneeContract { get; set; }
```

### 4.2 AssigneeContractConverter（新增类）

**文件**：`Application/Slots/AssigneeContractConverter.cs`

```csharp
public class AssigneeContractConverter
{
    private readonly SlotVariableConverter _slotConverter;
    private readonly ILogger<AssigneeContractConverter> _logger;

    public AssigneeContractConverter(
        SlotVariableConverter slotConverter,
        ILogger<AssigneeContractConverter> logger)
    {
        _slotConverter = slotConverter;
        _logger        = logger;
    }

    public SlotConversionResult Convert(
        AssigneeContract contract,
        Dictionary<string, NodeSemanticInfo> semanticMap,
        Dictionary<string, object> businessVariables = null)   // ← 必须透传，供 conditionalOn 求值
    {
        var roleDict = contract.Roles
            .GroupBy(r => r.RoleKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var slotSelections = new List<SlotSelection>();

        foreach (var (_, nodeInfo) in semanticMap)
        {
            if (string.IsNullOrWhiteSpace(nodeInfo.RoleKey)) continue;
            if (!roleDict.TryGetValue(nodeInfo.RoleKey, out var role)) continue;

            foreach (var slot in nodeInfo.Slots ?? new List<SlotDefinition>())
            {
                if (!string.Equals(slot.Mode, role.Mode, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "RoleKey [{RoleKey}] mode [{RoleMode}] 与 slot [{SlotKey}] " +
                        "mode [{SlotMode}] 不一致，已跳过",
                        role.RoleKey, role.Mode, slot.SlotKey, slot.Mode);
                    continue;
                }

                slotSelections.Add(new SlotSelection
                {
                    SlotKey = slot.SlotKey,
                    Users   = role.Users
                });
            }
        }

        var allSlotDefs = semanticMap.Values
            .Where(n => n.Slots != null)
            .SelectMany(n => n.Slots)
            .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // ⚠️ 必须传 businessVariables，供 SlotVariableConverter 内部 conditionalOn 求值
        return _slotConverter.Convert(slotSelections, allSlotDefs, businessVariables);
    }
}
```

### 4.3 ProcessLifecycleAppService 变更

Step 3 替换为分支判断，`AssigneeContract` 优先：

```csharp
SlotConversionResult initConversionResult;

if (request.AssigneeContract?.Roles?.Any() == true)
{
    var semanticMap = await _slotConfigProvider
        .GetNodeSemanticMapAsync(processDefinitionKey);

    initConversionResult = _assigneeContractConverter.Convert(
        request.AssigneeContract,
        semanticMap,
        request.BusinessVariables);   // ← 透传 businessVariables

    _logger.LogInformation(
        "AssigneeContract 路径: BusinessId={BusinessId}, 角色数={Count}",
        request.BusinessId, request.AssigneeContract.Roles.Count);
}
else
{
    initConversionResult = await ConvertInitialSlotsAsync(
        request.InitialSlotSelections, processDefinitionKey);
}
```

---

## 5. loopAssignments（结构化多实例选人）

### 5.1 数据模型

**文件**：`StartProcessRequest.cs`（追加）

```csharp
public class LoopAssignments
{
    public List<LoopItem> Items { get; set; } = new();
}

public class LoopItem
{
    /// <summary>
    /// 循环 Key，对应 BPMN multiInstance 节点标记
    /// 投影变量名格式：{loopKey}_{roleKey}_list
    /// </summary>
    public string LoopKey { get; set; }

    /// <summary>业务标识（可选，用于审计）</summary>
    public string BusinessKey { get; set; }

    public List<RoleAssignment> Roles { get; set; } = new();
}
```

**`StartProcessRequest` 追加**：

```csharp
public LoopAssignments LoopAssignments { get; set; }
```

### 5.2 LoopAssignmentProjector（新增类）

**文件**：`Application/Slots/LoopAssignmentProjector.cs`

```csharp
public class LoopAssignmentProjector
{
    private readonly ILogger<LoopAssignmentProjector> _logger;

    public LoopAssignmentProjector(ILogger<LoopAssignmentProjector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 投影规则：{loopKey}_{roleKey}_list = List&lt;string&gt;
    /// BPMN：flowable:collection="${dept_review_loop_reviewer_list}"
    ///       flowable:elementVariable="currentReviewer"
    /// </summary>
    public Dictionary<string, object> Project(LoopAssignments assignments)
    {
        if (assignments?.Items == null || !assignments.Items.Any())
            return new Dictionary<string, object>();

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in assignments.Items)
        {
            if (string.IsNullOrWhiteSpace(item.LoopKey)) continue;

            foreach (var role in item.Roles ?? new List<RoleAssignment>())
            {
                if (string.IsNullOrWhiteSpace(role.RoleKey)) continue;
                if (role.Users == null || !role.Users.Any()) continue;

                var varName = $"{item.LoopKey}_{role.RoleKey}_list";

                if (result.TryGetValue(varName, out var existing)
                    && existing is List<string> existingList)
                    existingList.AddRange(role.Users);
                else
                    result[varName] = new List<string>(role.Users);

                _logger.LogDebug("LoopAssignment 投影: {VarName} = [{Users}]",
                    varName, string.Join(",", role.Users));
            }
        }

        return result;
    }
}
```

### 5.3 ProcessLifecycleAppService 集成

`BuildStartVariables` 中在 Slot 变量之后、框架变量之前追加：

```csharp
if (request.LoopAssignments?.Items?.Any() == true)
{
    var loopVars = _loopAssignmentProjector.Project(request.LoopAssignments);
    foreach (var kv in loopVars)
        variables[kv.Key] = kv.Value;
}
```

---

## 6. 变量投影统一规则

| 来源 | 投影器 | 变量类型 | 优先级 |
|---|---|---|---|
| `AssigneeContract` | `AssigneeContractConverter` → `SlotVariableConverter` | `single`→`string`；`multiple`→`List<string>` | 1 |
| `LoopAssignments` | `LoopAssignmentProjector` | 始终 `List<string>` | 2（独立命名空间） |
| `BusinessVariables` | 直接写入 | 原样保留 | 3 |
| `InitialSlotSelections` | `SlotVariableConverter` | `single`→`string`；`multiple`→`List<string>` | Fallback |

框架内置变量（`frameworkCallbackUrl` / `businessId` / `processDefinitionKey`）最后写入，不可被覆盖。

**冲突处理**：`AssigneeContract` 与 `InitialSlotSelections` 同时传入时，`AssigneeContract` 完全优先，写 Warning 日志。

---

## 7. NextSlotSelections 兼容保留

**不改动任何现有逻辑**，在 `BuildCompletionVariablesAsync` 方法头追加注释：

```csharp
// ── 关于 NextSlotSelections ──────────────────────────────────────
// 此字段是唯一最终生效的人员来源。
// 全自动流程（AssigneeContract）：处理人已在启动时注入，无需此字段。
// 半自动流程：前端读取 recommendedUsers 初始化选人区，用户确认后提交此字段。
// 旧流程：保持原有逐节点选人行为不变。
// Phase 1 不加 [Obsolete]，不删除，不修改任何逻辑。
```

---

## 8. 节点完成判定

**文件**：`ProcessQueryAppService.cs`（新增方法）

```csharp
/// <summary>
/// 判断指定节点是否已完成
/// 规则：查询流程实例当前活跃任务，若 taskDefinitionKey 不在活跃列表中 → 节点已完成
/// 适用：单任务节点、会签（multiInstance）、并行网关分支
/// 不缓存，每次直接查询 Flowable
/// </summary>
public async Task<bool> IsNodeCompletedAsync(
    string processInstanceId,
    string taskDefinitionKey)
{
    var activeTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
    {
        ProcessInstanceId = processInstanceId
    });

    return !activeTasks.Any(t => string.Equals(
        t.TaskDefinitionKey, taskDefinitionKey,
        StringComparison.OrdinalIgnoreCase));
}
```

---

## 9. 回调机制（on_complete 极简版）

Phase 1 只支持 `callbackTiming = on_complete`，节点完全结束后调用业务系统一次。不实现重试、幂等键、回调状态机。

### 9.1 触发方式

BPMN 目标节点完成后追加 Flowable HTTP ServiceTask → 调 `CallbackController` → `ProcessCallbackAppService`。

### 9.2 ProcessCallbackAppService 变更

`HandleFlowableCallbackAsync` 入口新增分支：

```csharp
var callbackType = dto.Variables
    ?.GetValueOrDefault("callbackType")?.ToString();

if (string.Equals(callbackType, "node_on_complete",
    StringComparison.OrdinalIgnoreCase))
{
    await HandleNodeOnCompleteCallbackAsync(dto);
    return;
}
// 继续现有流程结束回调逻辑...
```

```csharp
private async Task HandleNodeOnCompleteCallbackAsync(FlowableCallbackDto dto)
{
    var taskDefinitionKey = dto.Variables
        ?.GetValueOrDefault("callbackNodeKey")?.ToString();
    if (string.IsNullOrWhiteSpace(taskDefinitionKey)) return;

    var isCompleted = await _queryService.IsNodeCompletedAsync(
        dto.ProcessInstanceId, taskDefinitionKey);

    if (!isCompleted)
    {
        _logger.LogWarning(
            "节点 [{NodeKey}] 尚未完成，跳过 on_complete 回调。ProcessInstanceId={Id}",
            taskDefinitionKey, dto.ProcessInstanceId);
        return;
    }

    var callbackUrl = dto.Variables
        ?.GetValueOrDefault("nodeCallbackUrl")?.ToString();
    if (string.IsNullOrWhiteSpace(callbackUrl)) return;

    // 单次 HTTP POST，无重试，无幂等（Phase 1 限制）
    await CallBusinessSystemOnceAsync(callbackUrl, dto);
}
```

---

## 10. ES 写入轻量补偿

**文件**：`ProcessLifecycleAppService.cs`，替换现有 ES 写入的 `catch` 块。

```csharp
catch (Exception ex)
{
    _logger.LogError(
        ex,
        "[ES_WRITE_FAIL] 流程启动成功但 ES 元数据写入失败，尝试一次重试。" +
        "ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
        processInstance.Id, request.BusinessId);

    try
    {
        await Task.Delay(500);
        await _esService.IndexProcessMetadataAsync(esDocument);

        _logger.LogWarning(
            "[ES_WRITE_RETRY_SUCCESS] ES 写入重试成功: ProcessInstanceId={ProcessInstanceId}",
            processInstance.Id);
    }
    catch (Exception retryEx)
    {
        _logger.LogCritical(
            retryEx,
            "[ES_WRITE_ORPHAN] 流程实例已在 Flowable 启动但 ES 写入两次均失败。" +
            "ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}。" +
            "请运维人员通过对账 Job 修复或手动补写 ES 记录。",
            processInstance.Id, request.BusinessId);

        throw new BusinessException(
            $"流程已启动（ProcessInstanceId={processInstance.Id}），" +
            $"但元数据写入失败，请联系管理员补录。",
            "PROCESS_METADATA_INDEX_ORPHAN");
    }
}
```

> **一致性空洞**：仅 1 次同步重试。重试失败后 Flowable 中存在无 ES 记录的孤儿实例，错误码 `PROCESS_METADATA_INDEX_ORPHAN` 应配置专属运维告警。

---

## 11. 推荐处理人机制（V1.2 新增）

### 11.1 设计边界

| 层 | 职责 | 不做 |
|---|---|---|
| slotConfig | 定义 `restrictToRecommended` 配置项（bool）；不存储推荐人名单 | 不存固定推荐人 |
| `StartProcessRequest` | 携带实例级推荐人 `recommendedAssignees`（动态注入） | 不做校验，不影响执行变量 |
| `ProcessMetadataDocument` | 存储推荐人快照 `RecommendedAssigneesSnapshot` | 不参与 Flowable 变量投影 |
| `ProcessQueryAppService` | 返回当前节点时合并 `recommendedUsers` 和 `restrictToRecommended` | 不做人员校验 |
| `TaskExecutionAppService` | 写审计记录时记录越界标记；不拦截 | 不修改 `NextSlotSelections` 转换逻辑 |
| 前端 | 读取推荐人初始化选人区；读取 `restrictToRecommended` 控制 UI 范围 | 最终提交 `NextSlotSelections` |

### 11.2 slotConfig 新增字段

**文件**：`BpmnDeploymentDto.cs`，`NodeSlotConfig` 和对应的 `SlotDefinition` 均追加字段。

**`NodeSlotConfig`（部署时 DTO）追加**：

```csharp
/// <summary>
/// 是否限制只能从推荐范围内选人
/// 前端控制 UI 范围，后端不强拦截，只做审计记录
/// </summary>
public bool RestrictToRecommended { get; set; } = false;
```

**`SlotDefinition`（`ProcessMetadataDocument.cs`）追加**：

```csharp
/// <summary>
/// 是否限制只能从推荐范围内选人
/// 前端读取控制 UI；后端只在审计时记录是否越界，不拦截
/// </summary>
public bool RestrictToRecommended { get; set; } = false;
```

**`MergeSlotConfig` 追加映射**（`BpmnDeploymentAppService.cs`）：

```csharp
// 在现有 slot 映射末尾追加
slot.RestrictToRecommended = slotConfig.RestrictToRecommended;
```

### 11.3 StartProcessRequest 新增推荐人字段

**文件**：`StartProcessRequest.cs`（追加）

```csharp
/// <summary>
/// 实例级推荐处理人（动态注入，非执行约束）
/// Key = slotKey，Value = 推荐人员列表
/// 前端读取后初始化选人区，最终仍由用户通过 NextSlotSelections 确认
/// </summary>
public Dictionary<string, List<string>> RecommendedAssignees { get; set; }
    = new Dictionary<string, List<string>>();
```

### 11.4 ProcessMetadataDocument 新增快照字段

**文件**：`ProcessMetadataDocument.cs`，`ProcessMetadataDocument` 类追加：

```csharp
/// <summary>
/// 启动时传入的推荐处理人快照（Key = slotKey）
/// 节点推进时供前端读取展示，不参与 Flowable 变量投影
/// </summary>
public Dictionary<string, List<string>> RecommendedAssigneesSnapshot { get; set; }
    = new Dictionary<string, List<string>>();
```

### 11.5 ProcessLifecycleAppService 写入快照

**文件**：`BuildProcessMetadataDocument` 方法中追加：

```csharp
RecommendedAssigneesSnapshot = request.RecommendedAssignees
    ?? new Dictionary<string, List<string>>()
```

### 11.6 ProcessQueryAppService 合并推荐人到当前节点

**文件**：`ProcessQueryAppService.cs`，`BuildCurrentNodesAsync` 中，在构建 `CurrentNodeDto` 时追加推荐人合并。

**`CurrentNodeDto` 新增字段**（`ProcessQueryDto.cs`）：

```csharp
/// <summary>当前节点各 slot 的推荐处理人（Key = slotKey）</summary>
public Dictionary<string, List<string>> RecommendedUsers { get; set; }
    = new Dictionary<string, List<string>>();

/// <summary>各 slot 是否限制只能从推荐范围内选人（Key = slotKey）</summary>
public Dictionary<string, bool> RestrictToRecommended { get; set; }
    = new Dictionary<string, bool>();
```

**`BuildCurrentNodesAsync` 追加逻辑**：在构建每个 `CurrentNodeDto` 时：

```csharp
// 从实例 metadata 的快照中取当前节点各 slot 的推荐人
var recommendedUsers = new Dictionary<string, List<string>>();
var restrictMap = new Dictionary<string, bool>();

if (nodeInfo?.Slots != null && metadata.RecommendedAssigneesSnapshot != null)
{
    foreach (var slot in nodeInfo.Slots)
    {
        if (metadata.RecommendedAssigneesSnapshot
            .TryGetValue(slot.SlotKey, out var recommended))
        {
            recommendedUsers[slot.SlotKey] = recommended;
        }
        restrictMap[slot.SlotKey] = slot.RestrictToRecommended;
    }
}

return new CurrentNodeDto
{
    // ... 现有字段不变 ...
    RecommendedUsers     = recommendedUsers,
    RestrictToRecommended = restrictMap
};
```

> `BuildCurrentNodesAsync` 需要接收 `metadata` 参数。现有签名为 `(List<FlowableTask>, string processDefinitionKey)`，追加第三个参数 `ProcessMetadataDocument metadata`，调用方 `GetProcessProgressAsync` 已有 `metadata`，直接透传。

### 11.7 TaskExecutionAppService 审计感知（不拦截）

**文件**：`TaskExecutionAppService.cs`，写审计记录（`WriteAuditRecordSafeAsync` 或对应位置）时追加越界判定字段。

**`ProcessAuditRecord` 新增字段**（`ProcessMetadataDocument.cs`）：

```csharp
/// <summary>
/// 本次提交中是否有人员越出推荐范围
/// null = 该节点无推荐人配置或 restrictToRecommended=false（不适用）
/// true = 至少一个 slot 提交了推荐范围外的人员
/// false = 所有 slot 均在推荐范围内
/// </summary>
public bool? HasOutOfRecommendedRange { get; set; }

/// <summary>
/// 本次完成任务时各 slot 的推荐人快照
/// 用于审计对比，Key = slotKey
/// </summary>
public Dictionary<string, List<string>> RecommendedUsersSnapshot { get; set; }
    = new Dictionary<string, List<string>>();

/// <summary>
/// 各 slot 的 restrictToRecommended 配置值快照
/// Key = slotKey
/// </summary>
public Dictionary<string, bool> RestrictToRecommendedSnapshot { get; set; }
    = new Dictionary<string, bool>();
```

**越界判定逻辑**（写审计记录前执行）：

```csharp
/// <summary>
/// 判断本次提交是否有人员越出推荐范围
/// 只在 restrictToRecommended=true 的 slot 上判定，其余 slot 忽略
/// 流程中心不拦截，只记录
/// </summary>
private (bool? hasOutOfRange, Dictionary<string, List<string>> recommendedSnapshot,
         Dictionary<string, bool> restrictSnapshot)
    EvaluateRecommendedRange(
        List<SlotSelection> nextSlotSelections,
        List<SlotDefinition> slotDefs,
        Dictionary<string, List<string>> recommendedSnapshot)
{
    if (recommendedSnapshot == null || !recommendedSnapshot.Any())
        return (null, new(), new());

    var restrictSnapshot = slotDefs
        .Where(d => recommendedSnapshot.ContainsKey(d.SlotKey))
        .ToDictionary(d => d.SlotKey, d => d.RestrictToRecommended);

    // 只检查 restrictToRecommended=true 的 slot
    var restrictedSlots = slotDefs
        .Where(d => d.RestrictToRecommended
               && recommendedSnapshot.ContainsKey(d.SlotKey))
        .ToList();

    if (!restrictedSlots.Any())
        return (null, recommendedSnapshot, restrictSnapshot);

    var selectionDict = (nextSlotSelections ?? new List<SlotSelection>())
        .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    bool hasOutOfRange = false;

    foreach (var slot in restrictedSlots)
    {
        if (!selectionDict.TryGetValue(slot.SlotKey, out var selection)) continue;
        if (!recommendedSnapshot.TryGetValue(slot.SlotKey, out var recommended)) continue;

        var outOfRange = selection.Users
            .Any(u => !recommended.Contains(u, StringComparer.OrdinalIgnoreCase));

        if (outOfRange)
        {
            hasOutOfRange = true;
            _logger.LogWarning(
                "[RECOMMEND_RANGE_EXCEEDED] SlotKey={SlotKey} 提交了推荐范围外人员。" +
                "Submitted={Submitted}, Recommended={Recommended}",
                slot.SlotKey,
                string.Join(",", selection.Users),
                string.Join(",", recommended));
        }
    }

    return (hasOutOfRange, recommendedSnapshot, restrictSnapshot);
}
```

**审计记录写入时追加**：

```csharp
var (hasOutOfRange, recSnapshot, restrictSnapshot) = EvaluateRecommendedRange(
    request.NextSlotSelections,
    currentSlotDefs,                          // 当前节点 slot 定义
    metadata.RecommendedAssigneesSnapshot);   // 实例推荐人快照

// 写入 ProcessAuditRecord
auditRecord.HasOutOfRecommendedRange      = hasOutOfRange;
auditRecord.RecommendedUsersSnapshot      = recSnapshot;
auditRecord.RestrictToRecommendedSnapshot = restrictSnapshot;
```

---

## 12. BPMN 适配指导

现有 BPMN 无需强制修改，旧路径继续运行。

### 12.1 新流程 userTask 推荐配置

```xml
<userTask id="ut01_dept_head_handle" name="部门一把手处理"
          flowable:assignee="${deptHeadAssignee}">
  <extensionElements>
    <flowable:field name="nodeSemantic"       stringValue="DEPT_HEAD_HANDLE"/>
    <flowable:field name="pageCode"           stringValue="SelectionCollection/DeptHeadHandleForm"/>
    <flowable:field name="isConvergencePoint" stringValue="false"/>
    <flowable:field name="canReject"          stringValue="true"/>
    <flowable:field name="isRejectTarget"     stringValue="false"/>
    <flowable:field name="roleKey"            stringValue="dept_head"/>
    <flowable:field name="assigneeMode"       stringValue="single"/>
    <flowable:field name="callbackTiming"     stringValue="on_complete"/>
  </extensionElements>
</userTask>
```

### 12.2 完整 StartProcessRequest 示例（半自动流程 + 推荐人）

```json
{
  "businessType": "personnel_selection",
  "businessId": "BIZ-2024-001",
  "recommendedAssignees": {
    "dept_head_slot":                  ["EMP_001", "EMP_002"],
    "inspection_office_reviewer_slot": ["EMP_005"]
  },
  "businessVariables": {
    "needFeedback": true
  },
  "callback": {
    "url": "https://biz-system.internal/api/process-callback",
    "timeoutSeconds": 30
  }
}
```

### 12.3 完整 StartProcessRequest 示例（全自动流程）

```json
{
  "businessType": "personnel_selection",
  "businessId": "BIZ-2024-002",
  "assigneeContract": {
    "roles": [
      { "roleKey": "dept_head",                  "mode": "single",   "users": ["EMP_001"] },
      { "roleKey": "inspection_office_reviewer", "mode": "single",   "users": ["EMP_005"] }
    ]
  },
  "loopAssignments": {
    "items": [
      {
        "loopKey": "joint_review_loop",
        "roles": [
          { "roleKey": "reviewer", "mode": "multiple",
            "users": ["EMP_010", "EMP_011", "EMP_012"] }
        ]
      }
    ]
  }
}
```

---

## 13. 已知限制与一致性空洞

| # | 限制 / 空洞 | 影响 | Phase 1 缓解 | Phase 2+ 根治 |
|---|---|---|---|---|
| L1 | ES 孤儿实例 | 用户看不到流程，可能重复提交 | 1 次同步重试 + CRITICAL 告警（§10） | Outbox Worker + 对账 Job |
| L2 | 节点级回调无重试 | 网络抖动导致回调丢失 | 只记 Error 日志 | Outbox 重试队列 + 幂等键 |
| L3 | loopAssignments 运行时不可动态增减 | 临时人员调整无法反映 | 转派（仅当前 task） | Phase 2 动态 LoopItem 管理 |
| L4 | conditionalOn 不支持（RoleAssignment 层） | 可选分支角色必须全量传入 | 多传无害 | conditionalOn Phase 2 |
| L5 | NodeOverride 不支持 | 同一角色不同节点无法差异化配置 | 使用不同 roleKey | NodeOverride Phase 2 |
| L6 | `RestrictToRecommended` 后端不强拦截 | 前端绕过限制时越界人员仍可提交 | 审计日志 `[RECOMMEND_RANGE_EXCEEDED]` | Phase 2 可选后端强校验 |
| L7 | `WriteAuditRecordSafeAsync` 吞异常 | 审计记录静默丢失 | 不在本 Patch 范围 | 补偿日志 + 专属告警 |
| L8 | AssigneeContract mode 不匹配时静默跳过 | 配置错误时无感知 | Warning 日志（§4.2） | 启动时前置校验 |

**运维告警配置建议**：
- `[ES_WRITE_ORPHAN]` → Critical
- `[ES_WRITE_RETRY_SUCCESS]` → Warning
- `PROCESS_METADATA_INDEX_ORPHAN` → Critical
- `[RECOMMEND_RANGE_EXCEEDED]` → Warning（审计感知）

---

## 14. 实施检查清单

### 新增文件

- `Application/Slots/AssigneeContractConverter.cs`
- `Application/Slots/LoopAssignmentProjector.cs`

### 修改文件

| 文件 | 改动位置 |
|---|---|
| `ProcessMetadataDocument.cs` | `NodeSemanticInfo` 追加 `RoleKey` / `AssigneeMode` / `CallbackTiming`；`SlotDefinition` 追加 `RestrictToRecommended`；`ProcessMetadataDocument` 追加 `RecommendedAssigneesSnapshot`；`ProcessAuditRecord` 追加审计越界字段 |
| `StartProcessRequest.cs` | 追加 `AssigneeContract` / `LoopAssignments` / `RecommendedAssignees` |
| `ProcessQueryDto.cs` | `CurrentNodeDto` 追加 `RecommendedUsers` / `RestrictToRecommended` |
| `BpmnDeploymentDto.cs` | `NodeSlotConfig` 追加 `RestrictToRecommended`；视需要追加 `RoleKey` / `AssigneeMode` / `CallbackTiming` |
| `ProcessLifecycleAppService.cs` | Step 3 分支；`BuildStartVariables` 追加 LoopAssignment；`BuildProcessMetadataDocument` 追加快照；ES catch 块替换 |
| `ProcessQueryAppService.cs` | `BuildCurrentNodesAsync` 签名追加 `metadata` 参数；合并推荐人；新增 `IsNodeCompletedAsync` |
| `TaskExecutionAppService.cs` | 方法头注释；追加 `EvaluateRecommendedRange`；审计记录写入越界字段 |
| `BpmnDeploymentAppService.cs` | `ParseNodeSemantics` 读取三个新字段；`MergeSlotConfig` 追加 `RestrictToRecommended` |
| `ProcessCallbackAppService.cs` | 新增 `HandleNodeOnCompleteCallbackAsync` 分支 |
| `Program.cs` | 注册 `AssigneeContractConverter` / `LoopAssignmentProjector` |

### 不修改文件

- `SlotVariableConverter.cs` — 完全不动
- `ElasticSearchSlotConfigProvider.cs` — 完全不动
- 所有 `IFlowable*` 接口和实现 — 完全不动
- 现有 BPMN 文件 — 旧路径继续运行

### 验收标准

1. **回归**：旧 `InitialSlotSelections` / `NextSlotSelections` 路径全量用例通过
2. **AssigneeContract**：`roleKey` 正确投影为 Flowable 变量；与 `InitialSlotSelections` 同传时 AssigneeContract 优先
3. **LoopAssignments**：`{loopKey}_{roleKey}_list` 变量正确写入
4. **推荐人写入**：`StartProcessRequest.RecommendedAssignees` 正确存入 `ProcessMetadataDocument.RecommendedAssigneesSnapshot`
5. **推荐人返回**：`GetProcessProgressAsync` 当前节点含 `recommendedUsers` 和 `restrictToRecommended`
6. **审计越界记录**：`restrictToRecommended=true` 的 slot 提交范围外人员时，审计记录 `HasOutOfRecommendedRange=true`，日志出现 `[RECOMMEND_RANGE_EXCEEDED]`；流程正常推进不被拦截
7. **ES 补偿**：模拟写入失败，单次重试成功出 Warning；重试失败出 Critical + `PROCESS_METADATA_INDEX_ORPHAN`
8. **节点完成判定**：`IsNodeCompletedAsync` 会签完成后返回 `true`，子任务仍在时返回 `false`

---

*— Patch Plan V1.2 · 修订：在 V1.1 基础上新增 §11 推荐处理人机制；T004 正式删除；T010 `businessVariables` 透传注意事项标注；tasks.md 核实结论内嵌至 §0。*
