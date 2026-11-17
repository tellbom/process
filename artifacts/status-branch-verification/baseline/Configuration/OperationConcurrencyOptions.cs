namespace FlowableWrapper.Configuration;

public sealed class OperationConcurrencyOptions
{
    public const string SectionName = "OperationConcurrency";

    public int Total { get; set; } = 8;
    public int ProcessStart { get; set; } = 8;
    public int TaskComplete { get; set; } = 4;
    public int PendingQuery { get; set; } = 2;
}
