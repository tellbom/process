using FlowableWrapper.Domain.Reliability;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

public class CallbackContractTests
{
    [Fact]
    public void Idempotency_key_is_stable_for_the_same_process_end_event()
    {
        var first = CallbackIdempotencyKey.ForProcessEnd(
            "process-42",
            "st03_framework_callback",
            "PROCESS_COMPLETED");
        var second = CallbackIdempotencyKey.ForProcessEnd(
            "process-42",
            "st03_framework_callback",
            "process_completed");

        Assert.Equal(first, second);
        Assert.Equal(
            "process-end:process-42:st03_framework_callback:process_completed",
            first);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    public void Retry_policy_only_retries_transient_http_failures(
        int statusCode,
        bool expectedRetry)
    {
        Assert.Equal(
            expectedRetry,
            CallbackRetryPolicy.IsRetryableStatus(statusCode));
    }

    [Fact]
    public void Retry_policy_moves_event_to_dead_letter_at_attempt_limit()
    {
        var decision = CallbackRetryPolicy.Decide(
            attemptCount: 5,
            maxAttempts: 5,
            httpStatus: 503,
            now: new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(CallbackEventStatus.DeadLetter, decision.Status);
        Assert.Null(decision.NextAttemptAt);
    }
}
