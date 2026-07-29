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
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    public static bool IsRetryableStatus(int statusCode)
        => statusCode == 408
           || statusCode == 429
           || statusCode >= 500;

    public static CallbackRetryDecision Decide(
        int attemptCount,
        int maxAttempts,
        int? httpStatus,
        DateTime now)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        if (attemptCount >= maxAttempts
            || (httpStatus.HasValue
                && !IsRetryableStatus(httpStatus.Value)))
        {
            return new CallbackRetryDecision(
                CallbackEventStatus.DeadLetter,
                null);
        }

        var delayIndex = Math.Clamp(attemptCount - 1, 0, Backoff.Length - 1);
        return new CallbackRetryDecision(
            CallbackEventStatus.RetryWaiting,
            now.Add(Backoff[delayIndex]));
    }
}
