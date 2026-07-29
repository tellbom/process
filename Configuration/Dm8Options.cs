namespace FlowableWrapper.Configuration;

public sealed class Dm8Options
{
    public const string SectionName = "Dm8";

    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "FLOW_RELIABILITY";
    public int CommandTimeoutSeconds { get; set; } = 30;
}
