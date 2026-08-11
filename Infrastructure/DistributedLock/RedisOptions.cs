namespace process.Infrastructure.DistributedLock
{
    public class RedisOptions
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string KeyPrefix { get; set; } = "process-center:";
        public int ConnectTimeoutMilliseconds { get; set; } = 5000;
        public int SyncTimeoutMilliseconds { get; set; } = 5000;
        public int AsyncTimeoutMilliseconds { get; set; } = 5000;
    }
}
