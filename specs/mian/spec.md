# Feature Specification: Process Center V1.1 交互节点补丁

**Feature Branch**: 未检测到 feature branch
**Created**: 2026-05-04
**Status**: Draft
**Input**: Claude 附件中的流程中心全自动化演进方案与 V1.1 交互节点索引

## User Stories & Testing

### User Story 1 - RoleKey 启动选人契约 (Priority: P1)

作为流程发起方，我希望启动流程时按业务角色传入处理人契约，而不是按节点 slotKey 逐个传人，以便新流程能逐步转向全流程一次性选人。

**Independent Test**: 使用带 `roleKey` 的节点语义配置启动流程，传入 `AssigneeContract.Roles` 后，Flowable 启动变量包含对应 assignee 变量；不传 `AssigneeContract` 时旧 `InitialSlotSelections` 仍可工作。

### User Story 2 - LoopAssignments 多实例投影 (Priority: P2)

作为流程建模方，我希望启动时能传入多实例集合变量，以便会签/循环节点从启动请求获得完整候选集合。

**Independent Test**: 启动请求传入 `LoopAssignments` 后，启动变量中出现对应集合变量；未传时不影响旧流程启动。

### User Story 3 - BPMN 部署读取交互字段 (Priority: P3)

作为流程部署方，我希望 BPMN 扩展字段或 slotConfig 能保存 `roleKey`、`assigneeMode`、`callbackTiming`，以便运行时逻辑有稳定元数据来源。

**Independent Test**: 部署含新字段的 BPMN 或 slotConfig 后，查询流程定义节点能看到这些字段。

### User Story 4 - 节点完成回调判定 (Priority: P4)

作为流程中心，我希望能判断某个节点是否真正完成，以便 on-complete 类型逻辑不会在会签/并行节点仍活跃时提前执行。

**Independent Test**: 对仍有同 taskDefinitionKey 活跃任务的流程返回未完成；对无同节点活跃任务的流程返回已完成。

## Requirements

- **FR-001**: 系统必须在 `NodeSemanticInfo` 中保存 `RoleKey`、`AssigneeMode`、`CallbackTiming`。
- **FR-002**: 系统必须在 `StartProcessRequest` 中支持 `AssigneeContract` 和 `LoopAssignments`。
- **FR-003**: 系统必须保持 `InitialSlotSelections` 与 `NextSlotSelections` 的兼容行为。
- **FR-004**: 系统不得修改 `SlotVariableConverter` 的职责和调用契约。
- **FR-005**: 系统必须通过 `AssigneeContractConverter` 将 RoleKey 契约展开为现有 slot 变量注入。
- **FR-006**: 系统必须通过 `LoopAssignmentProjector` 注入多实例集合变量。
- **FR-007**: 系统必须在 BPMN 部署与 slotConfig 合并时支持 V1.1 新字段。
- **FR-008**: 系统必须提供 `IsNodeCompletedAsync` 只读判断。
- **FR-009**: 系统必须增加最小化的 node-on-complete 回调分支，但不得引入 `NodeAction` 或 `CallbackSpec`。
- **FR-010**: ES 写入失败策略仅限 V1.1 指定的一次同步重试、CRITICAL 日志和 `PROCESS_METADATA_INDEX_ORPHAN` 错误码；不得引入 Outbox。

## Key Entities

- **NodeSemanticInfo**: BPMN userTask 的语义元数据，V1.1 扩展角色和回调时机字段。
- **AssigneeContract**: 启动时按角色传入的人员契约。
- **RoleAssignment**: 单个 RoleKey 对应的处理人列表与模式。
- **LoopAssignments**: 启动时传入的多实例集合变量契约。
- **LoopItem**: 多实例集合中的单个成员。

## Assumptions

- Flowable 仍是唯一运行态真相源。
- ES 语义索引仍是流程定义元数据来源。
- V1.1 是补丁范围，不承接 V1.0 中的长期架构项。
