# Tasks: Process Center V1.1 交互节点补丁

**Input**: `C:\Users\24203\Downloads\process_center_evolution_plan.md`, `C:\Users\24203\Downloads\Patch Plan V1.1 — 交互节点索引.md`
**Prerequisites**: 当前仓库代码结构；本 feature 尚无 `plan.md` / `spec.md`，本任务拆分以附件 V1.1 为事实来源
**Tests**: 未显式要求 TDD；本清单只包含实现与验证任务
**Scope Guard**: 本轮不引入 Outbox、iframe + postMessage、NodeAction / CallbackSpec、Saga、fallback 补人、NodeOverride、each_instance 语义

## Format: `[ID] [P?] [Story] Description`

- **[P]**: 可并行，任务写入不同文件且不依赖未完成任务
- **[Story]**: 用户故事编号
- 每个任务都包含明确文件路径

---

## Phase 1: Setup (Shared Context)

**Purpose**: 固化 V1.1 补丁范围，避免把 V1.0 长期演进项误并入当前实现。

- [ ] T001 Review V1.1 scope decisions and record exclusions in `specs/001-process-center-spec/tasks.md`
- [ ] T002 Inspect current DTO, ES document, slot converter, lifecycle, query, callback, deployment, and DI entry points in `Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`, `Application/Dtos/StartProcessRequest.cs`, `Application/Slots/SlotVariableConverter.cs`, `Application/Services/ProcessLifecycleAppService.cs`, `Application/Services/ProcessQueryAppService.cs`, `Application/Services/ProcessCallbackAppService.cs`, `Application/Services/BpmnDeploymentAppService.cs`, and `Program.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: 先完成共享契约与枚举模型，后续转换、启动、部署、查询回调都依赖这些类型。

**Critical**: 此阶段完成前，不开始任何用户故事实现。

- [ ] T003 [P] Add string `RoleKey`, `AssigneeMode`, and `CallbackTiming` fields plus XML comments to `NodeSemanticInfo` in `Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`
- [ ] T005 [P] Add `AssigneeContract`, `RoleAssignment`, `LoopAssignments`, and `LoopItem` request models in `Application/Dtos/StartProcessRequest.cs`
- [ ] T006 Add `AssigneeContract` and `LoopAssignments` properties to `StartProcessRequest` in `Application/Dtos/StartProcessRequest.cs`
- [ ] T007 Confirm legacy `InitialSlotSelections` and `NextSlotSelections` remain supported and not marked obsolete in `Application/Dtos/StartProcessRequest.cs` and `Application/Dtos/CompleteTaskRequest.cs`

**Checkpoint**: Shared contract types compile and legacy request fields remain available.

---

## Phase 3: User Story 1 - RoleKey 启动选人契约 (Priority: P1) MVP

**Goal**: 启动流程时可通过 `AssigneeContract` 按角色注入处理人，并复用现有 `SlotVariableConverter` 投影到 Flowable 变量。

**Independent Test**: 使用包含 `roleKey` 的节点语义配置启动流程，请求传入 `AssigneeContract.Roles` 后，首节点和后续节点所需 assignee 变量均进入启动变量；不传 `AssigneeContract` 时旧 `InitialSlotSelections` 仍可启动。

### Implementation for User Story 1

- [ ] T008 [P] [US1] Create `AssigneeContractConverter` class skeleton with constructor dependencies in `Application/Slots/AssigneeContractConverter.cs`
- [ ] T009 [US1] Implement role assignment lookup and validation by `RoleKey` and `AssigneeMode` in `Application/Slots/AssigneeContractConverter.cs`
- [ ] T010 [US1] Expand `AssigneeContract` roles into existing `SlotSelection` objects without modifying `SlotVariableConverter` in `Application/Slots/AssigneeContractConverter.cs`
- [ ] T011 [US1] Convert expanded role selections through `SlotVariableConverter.Convert(selections, slotDefs, businessVariables)`, passing `StartProcessRequest.BusinessVariables` for conditional slot evaluation, and return variables plus snapshots in `Application/Slots/AssigneeContractConverter.cs`
- [ ] T012 [US1] Add conflict handling so duplicate `RoleKey` assignments or missing required role users raise `BusinessException` in `Application/Slots/AssigneeContractConverter.cs`
- [ ] T013 [US1] Inject `AssigneeContractConverter` into `ProcessLifecycleAppService` constructor in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T014 [US1] Add Step 3 branching in `StartProcessAsync` so `AssigneeContract` is preferred and legacy `InitialSlotSelections` is used only when the new contract is absent in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T015 [US1] Preserve existing `ConvertInitialSlotsAsync` as the legacy path in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T016 [US1] Register `AssigneeContractConverter` in dependency injection in `Program.cs`

**Checkpoint**: RoleKey contract can start a process independently, while old slot startup still works.

---

## Phase 4: User Story 2 - LoopAssignments 多实例投影 (Priority: P2)

**Goal**: 启动流程时支持 `LoopAssignments` 注入会签/多实例集合变量，不引入 fallback 补人或 each_instance 回调语义。

**Independent Test**: 请求传入 `LoopAssignments` 后，启动变量包含每个 loop variable 对应的集合；不传 loop 配置时启动变量不增加多实例变量。

### Implementation for User Story 2

- [ ] T017 [P] [US2] Create `LoopAssignmentProjector` class skeleton in `Application/Slots/LoopAssignmentProjector.cs`
- [ ] T018 [US2] Implement loop assignment validation for empty variable names, empty item lists, and duplicate loop variables in `Application/Slots/LoopAssignmentProjector.cs`
- [ ] T019 [US2] Project `LoopAssignments` into Flowable-compatible variable objects in `Application/Slots/LoopAssignmentProjector.cs`
- [ ] T020 [US2] Inject `LoopAssignmentProjector` into `ProcessLifecycleAppService` constructor in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T021 [US2] Append loop assignment variables in `BuildStartVariables` after business variables and before framework variables in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T022 [US2] Ensure loop assignment variables do not overwrite framework variables `frameworkCallbackUrl`, `businessId`, or `processDefinitionKey` in `Application/Services/ProcessLifecycleAppService.cs`
- [ ] T023 [US2] Register `LoopAssignmentProjector` in dependency injection in `Program.cs`

**Checkpoint**: Multi-instance startup variables are injected from request data without changing task completion behavior.

---

## Phase 5: User Story 3 - BPMN 部署读取交互字段 (Priority: P3)

**Goal**: BPMN 部署时读取并保存 `roleKey`、`assigneeMode`、`callbackTiming`，让启动转换和节点完成判断有稳定元数据来源。

**Independent Test**: 部署包含三个扩展字段的 BPMN 后，`ProcessDefinitionSemanticDocument.NodeSemanticMap` 中对应节点包含 RoleKey、AssigneeMode、CallbackTiming。

### Implementation for User Story 3

- [ ] T024 [P] [US3] Parse `roleKey`, `assigneeMode`, and `callbackTiming` from Flowable extension fields in `ParseNodeSemantics` in `Application/Services/BpmnDeploymentAppService.cs`
- [ ] T025 [US3] Map parsed interaction fields into `NodeSemanticInfo` when building node semantic entries in `Application/Services/BpmnDeploymentAppService.cs`
- [ ] T026 [US3] Extend `NodeSlotConfig` parsing support for `roleKey`, `assigneeMode`, and `callbackTiming` in `Application/Dtos/BpmnDeploymentDto.cs`
- [ ] T027 [US3] Merge slotConfig interaction fields into `NodeSemanticInfo` with slotConfig taking precedence over BPMN extension fields in `Application/Services/BpmnDeploymentAppService.cs`
- [ ] T028 [US3] Add validation for legal `AssigneeMode` and `CallbackTiming` values in `ValidateSlotConfig` in `Application/Services/BpmnDeploymentAppService.cs`
- [ ] T029 [US3] Include interaction fields in deployment response node summaries in `Application/Dtos/BpmnDeploymentDto.cs` and `Application/Services/BpmnDeploymentAppService.cs`
- [ ] T030 [US3] Include interaction fields in process definition node query DTOs in `Application/Dtos/BpmnDeploymentDto.cs` and `Application/Services/BpmnDeploymentAppService.cs`

**Checkpoint**: New semantic fields round-trip through deployment and query without breaking existing slot and reject metadata.

---

## Phase 6: User Story 4 - 节点完成回调判定 (Priority: P4)

**Goal**: 为 on-complete 交互节点提供“节点是否真正完成”的只读判断和回调入口，不改变现有 `CompleteTask` 主流程。

**Independent Test**: 对普通节点完成后查询返回已完成；对仍有活跃任务的同一节点返回未完成；节点回调入口只在节点完成时继续处理。

### Implementation for User Story 4

- [ ] T031 [P] [US4] Add `IsNodeCompletedAsync` method signature and XML comments to `ProcessQueryAppService` in `Application/Services/ProcessQueryAppService.cs`
- [ ] T032 [US4] Implement `IsNodeCompletedAsync` by querying active Flowable tasks for process instance and task definition key in `Application/Services/ProcessQueryAppService.cs`
- [ ] T033 [US4] Inject `ProcessQueryAppService` into `ProcessCallbackAppService` constructor in `Application/Services/ProcessCallbackAppService.cs`
- [ ] T034 [US4] Add `HandleNodeOnCompleteCallbackAsync` method branch in `ProcessCallbackAppService` in `Application/Services/ProcessCallbackAppService.cs`
- [ ] T035 [US4] Gate `HandleNodeOnCompleteCallbackAsync` with `IsNodeCompletedAsync` before running callback logic in `Application/Services/ProcessCallbackAppService.cs`
- [ ] T036 [US4] Keep node callback branch side-effect minimal and avoid adding NodeAction or CallbackSpec models in `Application/Services/ProcessCallbackAppService.cs`
- [ ] T037 [US4] Add method-level comment to `BuildCompletionVariablesAsync` clarifying `NextSlotSelections` remains the legacy read path and no new assignment happens during completion in `Application/Services/TaskExecutionAppService.cs`

**Checkpoint**: Node completion detection exists and can be consumed without changing approve/reject behavior.

---

## Final Phase: Polish & Cross-Cutting Verification

**Purpose**: Validate the patch as a coherent V1.1 increment and keep future V1.0 evolution work out of this delivery.

- [ ] T038 [P] Run `dotnet build` from `process.csproj` and fix compile errors in touched files
- [ ] T039 [P] Review DI registrations and constructor call sites for `AssigneeContractConverter`, `LoopAssignmentProjector`, and `ProcessQueryAppService` in `Program.cs`, `Application/Services/ProcessLifecycleAppService.cs`, and `Application/Services/ProcessCallbackAppService.cs`
- [ ] T040 Verify no new Outbox, iframe, postMessage, NodeAction, CallbackSpec, Saga, fallback, NodeOverride, or each_instance implementation was introduced in `Application`, `Domain`, `Infrastructure`, and `Api`
- [ ] T041 Update API examples for `StartProcessRequest.AssigneeContract` and `StartProcessRequest.LoopAssignments` in `specs/001-process-center-spec/tasks.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup and blocks all user stories
- **US1 RoleKey startup (Phase 3)**: Depends on Foundational
- **US2 LoopAssignments (Phase 4)**: Depends on Foundational; can run after T005-T006 and in parallel with US1 except for shared edits in `ProcessLifecycleAppService.cs` and `Program.cs`
- **US3 BPMN deployment fields (Phase 5)**: Depends on T003; can run in parallel with US1 and US2 after shared model fields exist
- **US4 Node completion callback (Phase 6)**: Depends on Foundational; can run in parallel with US1-US3 except final DI review
- **Polish**: Depends on selected user stories being complete

### User Story Dependencies

- **US1 (P1)**: MVP; no dependency on US2-US4 after foundational models
- **US2 (P2)**: Independent loop variable injection; shares lifecycle and DI touchpoints with US1
- **US3 (P3)**: Supplies metadata for US1 but can be implemented independently once fields exist
- **US4 (P4)**: Independent query/callback branch; no dependency on startup contract implementation

### Parallel Opportunities

- T003 and T005 can run in parallel
- T008 and T017 can run in parallel after T005
- T024 can start after T003 while US1 converter work continues
- T031-T032 can run while startup and deployment work continues
- Final verification T038 and T039 can run in parallel after implementation is complete

---

## Parallel Example: User Story 1

```bash
Task: "Create AssigneeContractConverter class skeleton in Application/Slots/AssigneeContractConverter.cs"
Task: "Parse roleKey and assignee metadata in Application/Services/BpmnDeploymentAppService.cs"
Task: "Add IsNodeCompletedAsync in Application/Services/ProcessQueryAppService.cs"
```

---

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 so `AssigneeContract` can start a process.
3. Build and validate old `InitialSlotSelections` startup still works.
4. Stop and review before layering loop assignments or node callback behavior.

### Incremental Delivery

1. Add shared model fields and request contracts.
2. Deliver RoleKey startup conversion.
3. Add LoopAssignments projection.
4. Add BPMN deployment/query round-trip for interaction fields.
5. Add node-completion callback branch.
6. Run build and scope guard checks.

### Team Strategy

1. One developer owns shared models and lifecycle integration.
2. One developer owns deployment DTO and BPMN parsing.
3. One developer owns query/callback branch.
4. Coordinate edits to `Program.cs` and `ProcessLifecycleAppService.cs` at integration time.

---

## Notes

- `SlotVariableConverter` must remain unchanged for V1.1.
- `AssigneeContractConverter` must call `SlotVariableConverter.Convert` with the optional `businessVariables` argument from `StartProcessRequest.BusinessVariables` so existing `ConditionalOn` slot behavior remains intact.
- `NextSlotSelections` remains compatible for old flows and should not be marked obsolete in this patch.
- `AssigneeContractSnapshot` is intentionally not persisted because assignees are injected into Flowable variables.
- ES metadata write failure behavior is limited to one synchronous retry plus CRITICAL log and `PROCESS_METADATA_INDEX_ORPHAN`; full Outbox is explicitly out of scope.
