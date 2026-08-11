using FlowableWrapper.Configuration;
using FlowableWrapper.Infrastructure.Reliability;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

public class OperationConcurrencyGateTests
{
    [Fact]
    public async Task Second_start_waits_until_first_lease_is_released()
    {
        var gate = CreateGate(processStart: 1);
        var first = await gate.EnterProcessStartAsync(CancellationToken.None);

        var secondTask = gate.EnterProcessStartAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Canceled_wait_does_not_consume_a_slot()
    {
        var gate = CreateGate(taskComplete: 1);
        var first = await gate.EnterTaskCompleteAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = gate.EnterTaskCompleteAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        first.Dispose();
        using var next = await gate.EnterTaskCompleteAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Operation_limits_are_independent()
    {
        var gate = CreateGate(processStart: 1, pendingQuery: 1);
        using var start = await gate.EnterProcessStartAsync(CancellationToken.None);

        using var query = await gate.EnterPendingQueryAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Different_operations_share_the_total_limit()
    {
        var gate = CreateGate(
            total: 1,
            processStart: 1,
            pendingQuery: 1);
        var start = await gate.EnterProcessStartAsync(CancellationToken.None);

        var queryTask = gate.EnterPendingQueryAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(queryTask.IsCompleted);

        start.Dispose();
        using var query = await queryTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static OperationConcurrencyGate CreateGate(
        int total = 4,
        int processStart = 2,
        int taskComplete = 2,
        int pendingQuery = 2)
        => new(Options.Create(new OperationConcurrencyOptions
        {
            Total = total,
            ProcessStart = processStart,
            TaskComplete = taskComplete,
            PendingQuery = pendingQuery
        }));
}
