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

    [Fact]
    public void Node_idempotency_key_is_stable_and_separate_from_process_end()
    {
        var node = CallbackIdempotencyKey.ForNode(
            "process-42",
            "task-17",
            "NODE_COMPLETED");
        var duplicate = CallbackIdempotencyKey.ForNode(
            "PROCESS-42",
            "TASK-17",
            "node_completed");
        var processEnd = CallbackIdempotencyKey.ForProcessEnd(
            "process-42",
            "task-17",
            "node_completed");

        Assert.Equal("node:process-42:task-17:node_completed", node);
        Assert.Equal(node, duplicate);
        Assert.NotEqual(processEnd, node);
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
            now: new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc),
            retryBaseSeconds: 5,
            retryMaxSeconds: 900,
            jitterPercent: 0);

        Assert.Equal(CallbackEventStatus.DeadLetter, decision.Status);
        Assert.Null(decision.NextAttemptAt);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(5, 120)]
    [InlineData(6, 300)]
    [InlineData(7, 600)]
    [InlineData(8, 900)]
    public void Retry_policy_uses_the_production_backoff_sequence(
        int attemptCount,
        int expectedSeconds)
    {
        var now = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc);
        var decision = CallbackRetryPolicy.Decide(
            attemptCount,
            maxAttempts: 9,
            httpStatus: 503,
            now,
            retryBaseSeconds: 5,
            retryMaxSeconds: 900,
            jitterPercent: 0);

        Assert.Equal(CallbackEventStatus.RetryWaiting, decision.Status);
        Assert.Equal(now.AddSeconds(expectedSeconds), decision.NextAttemptAt);
    }
}
