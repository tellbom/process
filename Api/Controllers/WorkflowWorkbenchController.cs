using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Application.Slots;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/flowable/workbench")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkflowWorkbenchController : ControllerBase
{
    private readonly WorkflowWorkbenchAppService _workbench;
    private readonly ProcessOperationsAppService _operations;
    private readonly IFlowableRuntimeService _runtime;
    private readonly IFlowableTaskService _tasks;
    private readonly IProcessSlotConfigProvider _slots;
    private readonly WorkflowNextStepAppService _nextStep;
    public WorkflowWorkbenchController(WorkflowWorkbenchAppService workbench, ProcessOperationsAppService operations, IFlowableRuntimeService runtime,
        IFlowableTaskService tasks, IProcessSlotConfigProvider slots, WorkflowNextStepAppService nextStep)
        => (_workbench, _operations, _runtime, _tasks, _slots, _nextStep) = (workbench, operations, runtime, tasks, slots, nextStep);

    [HttpGet("definitions")]
    public async Task<ActionResult<ApiResult<WorkflowDefinitionPage>>> Definitions(
        [FromQuery] string? keyword, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
        => Ok(ApiResult<WorkflowDefinitionPage>.Ok(await _workbench.GetDefinitionsAsync(keyword, pageIndex, pageSize)));

    [HttpGet("definitions/{key}/artifact")]
    public async Task<ActionResult<ApiResult<WorkflowDefinitionArtifact>>> Artifact(string key, [FromQuery] int? version = null)
        => Ok(ApiResult<WorkflowDefinitionArtifact>.Ok(await _workbench.GetArtifactAsync(key, version)));

    [HttpGet("definitions/{key}/readiness")]
    public async Task<ActionResult<ApiResult<WorkflowDefinitionReadiness>>> Readiness(string key)
        => Ok(ApiResult<WorkflowDefinitionReadiness>.Ok(await _workbench.GetReadinessAsync(key)));

    [HttpPost("compile")]
    public ActionResult<ApiResult<WorkflowArtifactResult>> Compile([FromBody] WorkflowArtifactRequest request)
        => Ok(ApiResult<WorkflowArtifactResult>.Ok(WorkflowArtifactCompiler.Compile(request)));

    [HttpPost("instances/{processInstanceId}/next-step")]
    public async Task<ActionResult<ApiResult<WorkflowNextStepDto>>> NextStep(string processInstanceId, [FromBody] WorkflowNextStepRequest request)
    {
        var state = await _operations.GetAsync(request.BusinessId, processInstanceId);
        if (!state.IsActive || !state.Tasks.Any(x => x.TaskId == request.TaskId))
            throw new BusinessException("当前任务已变化，请刷新后重试。", "TASK_NOT_ACTIVE");
        var task = await _tasks.GetTaskAsync(request.TaskId);
        if (task.ProcessInstanceId != processInstanceId) throw new BusinessException("任务与流程实例不匹配。", "TASK_INSTANCE_MISMATCH");
        var parts = task.ProcessDefinitionId.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var version)) throw new BusinessException("任务绑定的定义版本不可读取。", "INVALID_DEFINITION_VERSION");
        var slots = await _slots.GetSlotsForNodeAsync(parts[0], task.TaskDefinitionKey, version);
        var (preview, _) = await _nextStep.ResolveAsync(task, request.BusinessVariables, slots);
        return Ok(ApiResult<WorkflowNextStepDto>.Ok(preview));
    }

    [HttpGet("instances/{processInstanceId}/variables")]
    public async Task<ActionResult<ApiResult<Dictionary<string, object>>>> Variables(string processInstanceId, [FromQuery] string businessId)
    {
        var state = await _operations.GetAsync(businessId, processInstanceId);
        var variables = state.IsActive ? await _runtime.GetProcessVariablesAsync(processInstanceId) : new();
        return Ok(ApiResult<Dictionary<string, object>>.Ok(variables));
    }
}
