namespace process.Domain.DistributedLock
{
    public interface IDistributedLockService
    {
        Task<bool> TryAcquireAsync(string key, string value, TimeSpan expiry);
        Task<bool> ReleaseAsync(string key, string value);
    }
}
