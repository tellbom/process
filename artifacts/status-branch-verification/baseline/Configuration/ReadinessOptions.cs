namespace FlowableWrapper.Configuration;

public sealed class ReadinessOptions
{
    public const string SectionName = "Readiness";

    public bool Enabled { get; set; } = true;
    public bool RequireElasticSearch { get; set; } = true;
    public int RetryIntervalSeconds { get; set; } = 2;
    public int ProbeIntervalSeconds { get; set; } = 30;
    public int ConsecutiveFailureThreshold { get; set; } = 3;
}
