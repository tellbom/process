namespace FlowableWrapper.Domain.Reliability;

public sealed class WorkflowBusinessQuery
{
    public string? BusinessId { get; init; }
    public string? BusinessType { get; init; }
    public string? FlowState { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedTimeFrom { get; init; }
    public DateTime? CreatedTimeTo { get; init; }
    public int Start { get; init; }
    public int Size { get; init; } = 20;
    public DateTime? AfterCreatedAt { get; init; }
    public string? AfterProcessInstanceId { get; init; }
}
