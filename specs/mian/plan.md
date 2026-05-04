# Implementation Plan: Process Center V1.1 交互节点补丁

**Branch**: 未检测到 feature branch
**Date**: 2026-05-04
**Spec**: `specs/001-process-center-spec/spec.md`
**Input**: Claude 附件方案 `process_center_evolution_plan.md` 与 `Patch Plan V1.1 — 交互节点索引.md`

## Summary

本次补丁把 Claude 的 V1.1 交互节点索引拆成可执行实现节点，目标是在不改变现有 `SlotVariableConverter`、不废弃旧 `NextSlotSelections` 的前提下，引入启动时人员契约和节点完成判断能力。

## Technical Context

**Language/Version**: C# / .NET 8
**Primary Dependencies**: ASP.NET Core, Flowable 7.2 REST, Elasticsearch, Redis
**Storage**: Elasticsearch stores process metadata, semantic documents, and audit records; Flowable remains the runtime truth source
**Project Type**: Single ASP.NET Core backend project
**Key Paths**:

- `Domain/ElasticSearch/Documents/ProcessMetadataDocument.cs`
- `Application/Dtos/StartProcessRequest.cs`
- `Application/Dtos/BpmnDeploymentDto.cs`
- `Application/Slots/SlotVariableConverter.cs`
- `Application/Services/ProcessLifecycleAppService.cs`
- `Application/Services/BpmnDeploymentAppService.cs`
- `Application/Services/ProcessQueryAppService.cs`
- `Application/Services/ProcessCallbackAppService.cs`
- `Application/Services/TaskExecutionAppService.cs`
- `Program.cs`

## Scope

### In Scope

- Add `RoleKey`, `AssigneeMode`, and `CallbackTiming` to `NodeSemanticInfo`.
- Add `AssigneeContract`, `RoleAssignment`, `LoopAssignments`, and `LoopItem` to startup request models.
- Add `AssigneeContractConverter` that expands role assignments into existing slot selections and calls `SlotVariableConverter`.
- Add `LoopAssignmentProjector` that injects multi-instance collection variables at startup.
- Extend BPMN deployment parsing and slotConfig merge for V1.1 interaction fields.
- Add `IsNodeCompletedAsync` and a minimal node-on-complete callback branch.
- Register new services in DI and validate with `dotnet build`.

### Out of Scope

- Outbox.
- iframe and `postMessage`.
- `NodeAction` / `CallbackSpec`.
- Saga.
- fallback补人.
- `NodeOverride`.
- `each_instance` callback semantics.
- Changing `SlotVariableConverter`.
- Marking `NextSlotSelections` obsolete.

## Constitution Check

No project constitution was found in the current workspace. This plan follows the repository's existing service, DTO, and ES document layout.

## Project Structure

```text
Application/
  Dtos/
  Services/
  Slots/
Domain/
  ElasticSearch/Documents/
Infrastructure/
Api/
Program.cs
specs/001-process-center-spec/
```

## Complexity Tracking

No constitution violations recorded. The main integration risk is shared edits to `ProcessLifecycleAppService.cs` and `Program.cs`, so tasks isolate converter/projector work before lifecycle integration.
