using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Application.Services;

public sealed class ProcessOperationsAppService
{
    private readonly IElasticSearchService _es;
    private readonly IWorkflowReliabilityStore _store;
    private readonly IFlowableRuntimeService _runtime;
    private readonly IFlowableTaskService _tasks;
    private readonly IProcessSlotConfigProvider _slots;
    private readonly bool _dm8;
    private readonly ILogger<ProcessOperationsAppService> _logger;

    public ProcessOperationsAppService(IElasticSearchService es, IWorkflowReliabilityStore store,
        IFlowableRuntimeService runtime, IFlowableTaskService tasks,
        IProcessSlotConfigProvider slots, IOptions<Dm8Options> options, ILogger<ProcessOperationsAppService> logger)
    {
        _es = es; _store = store; _runtime = runtime; _tasks = tasks; _slots = slots;
        _dm8 = options.Value.Enabled;
        _logger = logger;
    }

    public async Task<ProcessOperationsDto> GetAsync(string businessId, string processInstanceId)
    {
        if (string.IsNullOrWhiteSpace(businessId) || string.IsNullOrWhiteSpace(processInstanceId))
            throw new BusinessException("businessId 和 processInstanceId 不能为空");
        var metadata = await _es.GetProcessMetadataAsync(processInstanceId);
        if (metadata == null || !string.Equals(metadata.BusinessId, businessId, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("流程实例不存在或与业务 ID 不匹配", "PROCESS_INSTANCE_NOT_FOUND");
        var latestId = _dm8
            ? (await _store.GetBusinessByBusinessIdAsync(businessId))?.ProcessInstanceId
            : (await _es.QueryProcessListAsync(new ProcessListRequest { BusinessId = businessId, PageSize = 1 }))
                .Items.FirstOrDefault()?.ProcessInstanceId;
        if (_dm8)
        {
            var bindings = await _store.GetBusinessesByProcessInstancesAsync(new[] { processInstanceId });
            if (bindings.TryGetValue(processInstanceId, out var binding))
            {
                metadata.Status = binding.FlowState;
                metadata.ProcessDefinitionVersion = binding.ProcessDefinitionVersion;
            }
        }
        var active = await GetActiveAsync(_runtime, processInstanceId);
        var result = new ProcessOperationsDto
        {
            BusinessId = businessId, ProcessInstanceId = processInstanceId,
            IsLatestRound = latestId == processInstanceId, IsActive = active,
            FlowState = active ? "running" : metadata.Status == "running" ? "not_active" : metadata.Status
        };
        if (!result.IsLatestRound || !active)
        {
            result.UnavailableReason = !active ? "该轮流程已结束或引擎中已无活动实例，仅可查看历史和处理投递。"
                : "该记录不是当前审批轮次，不能对其他轮次执行运维操作。";
            return result;
        }
        result.AvailableActions.Add("terminate");
        Dictionary<string, FlowableWrapper.Domain.ElasticSearch.NodeSemanticInfo> map;
        try { map = await _slots.GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey, metadata.ProcessDefinitionVersion); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Node configuration unavailable for operations. ProcessInstanceId={Instance}", processInstanceId);
            result.TaskConfigurationWarning = "节点配置暂不可用，任务转派已禁用，仍可按需收回当前流程。";
            map = new();
        }
        var start = 0;
        while (true)
        {
            var page = await _tasks.QueryTaskPageAsync(new FlowableTaskQuery { ProcessInstanceId = processInstanceId, Start = start, Size = 100 });
            foreach (var task in page.Items)
                result.Tasks.Add(new ProcessOperationTaskDto
                {
                    TaskId = task.Id, TaskDefinitionKey = task.TaskDefinitionKey, TaskName = task.Name,
                    Assignee = task.Assignee, CreateTime = task.CreateTime,
                    CandidateUsers = await _tasks.GetCandidateUsersAsync(task.Id),
                    CanReassign = map.TryGetValue(task.TaskDefinitionKey, out var node) && node.CanReassign
                });
            start += page.Items.Count;
            if (start >= page.Total) break;
            if (page.Items.Count == 0) throw new InvalidOperationException("当前任务分页提前结束，请刷新后重试");
        }
        return result;
    }

    public static async Task<bool> GetActiveAsync(IFlowableRuntimeService runtime, string instanceId)
    {
        try
        {
            var instance = await runtime.GetProcessInstanceAsync(instanceId);
            return instance != null && !instance.IsEnded;
        }
        catch (FlowableApiException ex) when (ex.StatusCode == 404) { return false; }
    }
}
