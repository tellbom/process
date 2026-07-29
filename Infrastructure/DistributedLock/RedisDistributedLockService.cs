namespace process.Infrastructure.DistributedLock
{
    using process.Domain.DistributedLock;
    using StackExchange.Redis;
    using Microsoft.Extensions.Options;

    public class RedisDistributedLockService : IDistributedLockService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RedisDistributedLockService> _logger;
        private readonly string _keyPrefix;

        public RedisDistributedLockService(
            IConnectionMultiplexer redis,
            IOptions<RedisOptions> options,
            ILogger<RedisDistributedLockService> logger)
        {
            _redis = redis;
            _keyPrefix = options.Value.KeyPrefix ?? "process-center:";
            _logger = logger;
        }

        public async Task<bool> TryAcquireAsync(string key, string value, TimeSpan expiry)
        {
            var db = _redis.GetDatabase();
            key = _keyPrefix + key;

            var acquired = await db.StringSetAsync(
                key,
                value,
                expiry,
                When.NotExists);

            if (acquired)
            {
                _logger.LogInformation(
                    "获取分布式锁成功: Key={Key}, Value={Value}",
                    key, value);
            }
            else
            {
                _logger.LogWarning(
                    "获取分布式锁失败: Key={Key}, Value={Value}",
                    key, value);
            }

            return acquired;
        }

        public async Task<bool> ReleaseAsync(string key, string value)
        {
            var db = _redis.GetDatabase();
            key = _keyPrefix + key;

            const string script = @"
if redis.call('get', KEYS[1]) == ARGV[1] then
    return redis.call('del', KEYS[1])
else
    return 0
end";

            var result = await db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key },
                new RedisValue[] { value });

            var released = (int)result == 1;

            if (released)
            {
                _logger.LogInformation(
                    "释放分布式锁成功: Key={Key}, Value={Value}",
                    key, value);
            }
            else
            {
                _logger.LogWarning(
                    "释放分布式锁失败或锁已失效: Key={Key}, Value={Value}",
                    key, value);
            }

            return released;
        }
    }
}
