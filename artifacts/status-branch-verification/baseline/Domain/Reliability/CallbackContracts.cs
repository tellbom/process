using System.Security.Cryptography;

namespace FlowableWrapper.Domain.Reliability;

public static class CallbackEventStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string RetryWaiting = "retry_waiting";
    public const string Succeeded = "succeeded";
    public const string DeadLetter = "dead_letter";
    public const string Cancelled = "cancelled";
}

public static class CallbackIdempotencyKey
{
    public static string ForNode(
        string processInstanceId,
        string callbackActivityId,
        string callbackType)
    {
        EnsureNotBlank(processInstanceId, nameof(processInstanceId));
        EnsureNotBlank(callbackActivityId, nameof(callbackActivityId));
        EnsureNotBlank(callbackType, nameof(callbackType));

        return string.Join(
            ':',
            "node",
            Normalize(processInstanceId),
            Normalize(callbackActivityId),
            Normalize(callbackType));
    }

    public static string ForProcessEnd(
        string processInstanceId,
        string callbackActivityId,
        string callbackType)
    {
        EnsureNotBlank(processInstanceId, nameof(processInstanceId));
        EnsureNotBlank(callbackActivityId, nameof(callbackActivityId));
        EnsureNotBlank(callbackType, nameof(callbackType));

        return string.Join(
            ':',
            "process-end",
            Normalize(processInstanceId),
            Normalize(callbackActivityId),
            Normalize(callbackType));
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();

    private static void EnsureNotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", parameterName);
    }
}

public sealed record CallbackRetryDecision(
    string Status,
    DateTime? NextAttemptAt);

public static class CallbackRetryPolicy
{
    private static readonly int[] BackoffMultipliers =
    {
        1, 3, 6, 12, 24, 60, 120, 180
    };

    public static bool IsRetryableStatus(int statusCode)
        => statusCode == 408
           || statusCode == 429
           || statusCode >= 500;

    public static CallbackRetryDecision Decide(
        int attemptCount,
        int maxAttempts,
        int? httpStatus,
        DateTime now,
        int retryBaseSeconds,
        int retryMaxSeconds,
        int jitterPercent)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryBaseSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(retryBaseSeconds));
        if (retryMaxSeconds < retryBaseSeconds)
            throw new ArgumentOutOfRangeException(nameof(retryMaxSeconds));

        if (attemptCount >= maxAttempts
            || (httpStatus.HasValue
                && !IsRetryableStatus(httpStatus.Value)))
        {
            return new CallbackRetryDecision(
                CallbackEventStatus.DeadLetter,
                null);
        }

        var delayIndex = Math.Clamp(
            attemptCount - 1,
            0,
            BackoffMultipliers.Length - 1);
        var delaySeconds = Math.Min(
            retryMaxSeconds,
            retryBaseSeconds * BackoffMultipliers[delayIndex]);
        var boundedJitter = Math.Clamp(jitterPercent, 0, 50);
        if (boundedJitter > 0)
        {
            var range = delaySeconds * boundedJitter / 100.0;
            var sample = RandomNumberGenerator.GetInt32(0, 10001) / 10000.0;
            delaySeconds = Math.Max(
                1,
                (int)Math.Round(delaySeconds - range + sample * range * 2));
        }

        return new CallbackRetryDecision(
            CallbackEventStatus.RetryWaiting,
            now.AddSeconds(delaySeconds));
    }
}
