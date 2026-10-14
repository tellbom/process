namespace FlowableWrapper.Application.Dtos;

/// <summary>按流程实例固定审批轮次的业务审签详情，不包含回调配置或流程变量。</summary>
public sealed class ProcessApprovalDetailsDto
{
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string? BusinessTitle { get; set; }
    public string ProcessDefinitionKey { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public int ApprovalRound { get; set; }
    /// <summary>流程中心元数据状态；不从最后一条审批动作推断整条流程结果。</summary>
    public string Status { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public double? DurationSeconds { get; set; }
    public List<ProcessApprovalTaskDto> Tasks { get; set; } = new();
    public List<ProcessApprovalAuditDto> AuditHistory { get; set; } = new();
}

/// <summary>Flowable 历史任务的实际执行状态，包含已结束和仍在执行的任务。</summary>
public sealed class ProcessApprovalTaskDto
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskDefinitionKey { get; set; } = string.Empty;
    public string? TaskName { get; set; }
    public string? Assignee { get; set; }
    /// <summary>active / completed / cancelled；completed 仅表示任务正常结束。</summary>
    public string Status { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    /// <summary>未结束时为 null，不将缺失时间伪造为零耗时。</summary>
    public double? DurationSeconds { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>流程中心记录的审批操作。通过 TaskId 与 Tasks 关联，不作为执行成功的独立凭据。</summary>
public sealed class ProcessApprovalAuditDto
{
    public string AuditId { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public string? TaskDefinitionKey { get; set; }
    public string? NodeSemantic { get; set; }
    public string? PageCode { get; set; }
    public string? Action { get; set; }
    public string? OperatorId { get; set; }
    public DateTime OperatedAt { get; set; }
    public string? Comment { get; set; }
    public string? RejectReason { get; set; }
    public string? RejectCode { get; set; }
    public string? RejectTargetNodeKey { get; set; }
    public List<SlotSelectionRecordDto> SlotSelections { get; set; } = new();
}
