using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;

namespace FlowableWrapper.Application.Services;

public sealed class WorkflowNextStepAppService
{
    private readonly IFlowableRepositoryService _repository;
    private readonly IFlowableRuntimeService _runtime;
    private readonly SlotVariableConverter _slots;
    public WorkflowNextStepAppService(IFlowableRepositoryService repository, IFlowableRuntimeService runtime, SlotVariableConverter slots)
        => (_repository, _runtime, _slots) = (repository, runtime, slots);

    public async Task<(WorkflowNextStepDto Preview, Dictionary<string, object> Variables)> ResolveAsync(
        FlowableTask task, Dictionary<string, object>? changes, List<SlotDefinition> slots)
    {
        var variables = new Dictionary<string, object>(await _runtime.GetProcessVariablesAsync(task.ProcessInstanceId));
        foreach (var entry in changes ?? new()) variables[entry.Key] = entry.Value;
        // 与正式通过接口的内置变量优先级一致，不能被业务输入覆盖。
        variables["isApproved"] = true;
        // 使用此任务真正绑定的定义，不使用最新版本，也不使用编辑中的画布。
        var xml = await _repository.GetBpmnXmlByDefinitionIdAsync(task.ProcessDefinitionId);
        var preview = WorkflowNextStepResolver.Resolve(xml, task.TaskDefinitionKey, variables, slots);
        if (preview.Resolved) preview.Slots = preview.Slots.Where(slot => _slots.EvaluateCondition(slot.ConditionalOn, variables)).ToList();
        return (preview, variables);
    }
}
