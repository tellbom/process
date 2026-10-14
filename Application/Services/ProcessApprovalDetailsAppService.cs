using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;

namespace FlowableWrapper.Application.Services;

/// <summary>业务系统审签查询，仅按实例读取，不依赖当前登录用户，不写入流程数据。</summary>
public sealed class ProcessApprovalDetailsAppService
{
    private readonly IElasticSearchService _esService;
    private readonly IFlowableHistoryService _historyService;

    public ProcessApprovalDetailsAppService(
        IElasticSearchService esService,
        IFlowableHistoryService historyService)
    {
        _esService = esService;
        _historyService = historyService;
    }

    public async Task<ProcessApprovalDetailsDto> GetApprovalDetailsAsync(string processInstanceId)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId))
            throw new BusinessException("processInstanceId 不能为空", "PROCESS_INSTANCE_ID_REQUIRED");

        var metadata = await _esService.GetProcessMetadataAsync(processInstanceId)
            ?? throw new ResourceNotFoundException("未找到流程实例", "PROCESS_INSTANCE_NOT_FOUND");

        var auditTask = _esService.QueryAuditRecordsByProcessInstanceIdAsync(processInstanceId);
        var historyTask = _historyService.QueryHistoricTasksAsync(new FlowableHistoricTaskQuery
        {
            ProcessInstanceId = processInstanceId
        });
        // 任一数据源失败都返回错误，避免把查询失败包装成“没有审批记录”。
        await Task.WhenAll(auditTask, historyTask);

        return new ProcessApprovalDetailsDto
        {
            ProcessInstanceId = metadata.ProcessInstanceId,
            BusinessId = metadata.BusinessId,
            BusinessType = metadata.BusinessType,
            BusinessTitle = metadata.BusinessTitle,
            ProcessDefinitionKey = metadata.ProcessDefinitionKey,
            RequestId = metadata.RequestId,
            ApprovalRound = metadata.ApprovalRound,
            Status = metadata.Status,
            CreatedBy = metadata.CreatedBy,
            CreatedTime = metadata.CreatedTime,
            CompletedTime = metadata.CompletedTime,
            DurationSeconds = metadata.CompletedTime.HasValue
                ? (metadata.CompletedTime.Value - metadata.CreatedTime).TotalSeconds
                : null,
            Tasks = historyTask.Result
                .Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .OrderBy(task => task.StartTime)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Select(task => new ProcessApprovalTaskDto
                {
                    TaskId = task.Id,
                    TaskDefinitionKey = task.TaskDefinitionKey,
                    TaskName = task.Name,
                    Assignee = task.Assignee,
                    Status = !task.EndTime.HasValue ? "active"
                        : string.IsNullOrEmpty(task.DeleteReason)
                          || string.Equals(task.DeleteReason, "completed", StringComparison.OrdinalIgnoreCase)
                            ? "completed" : "cancelled",
                    StartTime = task.StartTime,
                    EndTime = task.EndTime,
                    DurationSeconds = task.EndTime.HasValue
                        ? task.DurationInMillis.HasValue
                            ? task.DurationInMillis.Value / 1000d
                            : (task.EndTime.Value - task.StartTime).TotalSeconds
                        : null,
                    DeleteReason = task.DeleteReason
                }).ToList(),
            AuditHistory = auditTask.Result
                .Where(record => string.Equals(record.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .OrderBy(record => record.OperatedAt)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .Select(record => new ProcessApprovalAuditDto
                {
                    AuditId = record.Id,
                    TaskId = record.TaskId,
                    TaskDefinitionKey = record.TaskDefinitionKey,
                    NodeSemantic = record.NodeSemantic,
                    PageCode = record.PageCode,
                    Action = record.Action,
                    OperatorId = record.OperatorId,
                    OperatedAt = record.OperatedAt,
                    Comment = record.Comment,
                    RejectReason = record.RejectReason,
                    RejectCode = record.RejectCode,
                    RejectTargetNodeKey = record.RejectTargetNodeKey,
                    SlotSelections = record.SlotSelections?.Select(selection => new SlotSelectionRecordDto
                    {
                        SlotKey = selection.SlotKey,
                        Label = selection.Label,
                        Users = selection.Users?.ToList() ?? new List<string>()
                    }).ToList() ?? new List<SlotSelectionRecordDto>()
                }).ToList()
        };
    }
}
