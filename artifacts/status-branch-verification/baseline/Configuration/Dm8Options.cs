namespace FlowableWrapper.Configuration;

public sealed class Dm8Options
{
    public const string SectionName = "Dm8";

    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    // 空字符串表示不写模式前缀，直接使用连接当前模式。
    public string Schema { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ConnectionPoolSize { get; set; } = 128;
    public int ConnectionPoolTimeoutMilliseconds { get; set; } = 60000;
}
