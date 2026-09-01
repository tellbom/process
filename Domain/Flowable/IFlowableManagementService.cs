namespace FlowableWrapper.Domain.Flowable;

public interface IFlowableManagementService
{
    Task<FlowableDeadLetterJobPage> QueryDeadLetterJobsAsync(
        int start,
        int size,
        string? processInstanceId = null);

    Task<FlowableDeadLetterJob?> GetDeadLetterJobAsync(string jobId);

    Task<string?> GetDeadLetterJobExceptionStackTraceAsync(string jobId);

    Task MoveDeadLetterJobToExecutableAsync(string jobId);
}

public sealed class FlowableDeadLetterJobPage
{
    public List<FlowableDeadLetterJob> Data { get; set; } = new();
    public int Total { get; set; }
    public int Start { get; set; }
    public int Size { get; set; }
}

public sealed class FlowableDeadLetterJob
{
    public string Id { get; set; } = string.Empty;
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public string ProcessDefinitionId { get; set; } = string.Empty;
    public string ElementId { get; set; } = string.Empty;
    public string? ElementName { get; set; }
    public string? ExceptionMessage { get; set; }
    public int Retries { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CreateTime { get; set; }
    public string? JobType { get; set; }
    public string? TenantId { get; set; }
}
