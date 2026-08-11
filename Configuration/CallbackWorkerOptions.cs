namespace FlowableWrapper.Configuration;

public sealed class CallbackWorkerOptions
{
    public const string SectionName = "CallbackWorker";

    public bool Enabled { get; set; } = true;
    public int WorkerCount { get; set; } = 10;
    public int PollBatchSize { get; set; } = 50;
    public int PollIntervalMilliseconds { get; set; } = 500;
    public int PerDownstreamConcurrency { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 60;
    public int LeaseRenewIntervalSeconds { get; set; } = 20;
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int MaxRetryCount { get; set; } = 8;
    public int RetryBaseSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 900;
    public int RetryJitterPercent { get; set; } = 20;
}
