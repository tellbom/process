namespace FlowableWrapper.Configuration;

public sealed class CallbackWorkerOptions
{
    public const string SectionName = "CallbackWorker";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 500;
    public int BatchSize { get; set; } = 20;
    public int GlobalConcurrency { get; set; } = 20;
    public int PerDownstreamConcurrency { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 90;
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
}
