namespace FlowableWrapper.Application.Dtos;

public sealed class ProcessOperationsDto
{
    public string BusinessId { get; set; } = string.Empty;
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string FlowState { get; set; } = string.Empty;
    public bool IsLatestRound { get; set; }
    public bool IsActive { get; set; }
    public string? UnavailableReason { get; set; }
    public string? TaskConfigurationWarning { get; set; }
    public List<string> AvailableActions { get; set; } = new();
    public List<ProcessOperationTaskDto> Tasks { get; set; } = new();
}

public sealed class ProcessOperationTaskDto
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskDefinitionKey { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string? Assignee { get; set; }
    public List<string> CandidateUsers { get; set; } = new();
    public DateTime CreateTime { get; set; }
    public bool CanReassign { get; set; }
}
