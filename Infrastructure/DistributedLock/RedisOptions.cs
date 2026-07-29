namespace process.Infrastructure.DistributedLock
{
    public class RedisOptions
    {
        public string ConnectionString { get; set; }
        public string KeyPrefix { get; set; } = "process-center:";
    }
}
