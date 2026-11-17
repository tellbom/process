using System.Collections.Concurrent;

namespace FlowableWrapper.Infrastructure.Dm8;

public sealed class CallbackWorkerTelemetry
{
    private readonly ConcurrentQueue<CallbackDispatchSample> _samples = new();
    private readonly ConcurrentDictionary<string, int> _activeByDownstream =
        new(StringComparer.OrdinalIgnoreCase);
    private long _leaseLosses;
    private int _initialized;

    public bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    public void MarkInitialized()
        => Volatile.Write(ref _initialized, 1);

    public void DispatchStarted(string downstream)
        => _activeByDownstream.AddOrUpdate(downstream, 1, (_, value) => value + 1);

    public void DispatchCompleted(
        string downstream,
        bool succeeded,
        TimeSpan duration)
    {
        _activeByDownstream.AddOrUpdate(
            downstream,
            0,
            (_, value) => Math.Max(0, value - 1));
        _samples.Enqueue(new CallbackDispatchSample(
            DateTime.UtcNow,
            downstream,
            succeeded,
            duration.TotalMilliseconds));
        Prune(DateTime.UtcNow.AddMinutes(-5));
    }

    public void LeaseLost()
        => Interlocked.Increment(ref _leaseLosses);

    public CallbackWorkerTelemetrySnapshot Snapshot()
    {
        var now = DateTime.UtcNow;
        Prune(now.AddMinutes(-5));
        var lastMinute = _samples
            .Where(sample => sample.Timestamp >= now.AddMinutes(-1))
            .ToList();
        var downstreams = lastMinute
            .GroupBy(sample => sample.Downstream, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var durations = group
                    .Select(sample => sample.DurationMilliseconds)
                    .OrderBy(value => value)
                    .ToArray();
                var p95Index = durations.Length == 0
                    ? 0
                    : Math.Min(
                        durations.Length - 1,
                        (int)Math.Ceiling(durations.Length * 0.95) - 1);
                return new CallbackDownstreamTelemetry
                {
                    Downstream = group.Key,
                    Active = _activeByDownstream.TryGetValue(
                        group.Key, out var active)
                        ? active
                        : 0,
                    SuccessPerMinute = group.LongCount(sample => sample.Succeeded),
                    FailurePerMinute = group.LongCount(sample => !sample.Succeeded),
                    P95DurationMilliseconds =
                        durations.Length == 0 ? 0 : durations[p95Index]
                };
            })
            .OrderBy(item => item.Downstream, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var active in _activeByDownstream.Where(item => item.Value > 0))
        {
            if (downstreams.Any(item => string.Equals(
                    item.Downstream,
                    active.Key,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            downstreams.Add(new CallbackDownstreamTelemetry
            {
                Downstream = active.Key,
                Active = active.Value
            });
        }

        return new CallbackWorkerTelemetrySnapshot
        {
            SuccessPerMinute =
                lastMinute.LongCount(sample => sample.Succeeded),
            FailurePerMinute =
                lastMinute.LongCount(sample => !sample.Succeeded),
            LeaseLosses = Interlocked.Read(ref _leaseLosses),
            Downstreams = downstreams
        };
    }

    private void Prune(DateTime threshold)
    {
        while (_samples.TryPeek(out var sample)
               && sample.Timestamp < threshold)
            _samples.TryDequeue(out _);
    }

    private sealed record CallbackDispatchSample(
        DateTime Timestamp,
        string Downstream,
        bool Succeeded,
        double DurationMilliseconds);
}

public sealed class CallbackWorkerTelemetrySnapshot
{
    public long SuccessPerMinute { get; init; }
    public long FailurePerMinute { get; init; }
    public long LeaseLosses { get; init; }
    public IReadOnlyList<CallbackDownstreamTelemetry> Downstreams { get; init; } =
        Array.Empty<CallbackDownstreamTelemetry>();
}

public sealed class CallbackDownstreamTelemetry
{
    public string Downstream { get; init; } = string.Empty;
    public int Active { get; init; }
    public long SuccessPerMinute { get; init; }
    public long FailurePerMinute { get; init; }
    public double P95DurationMilliseconds { get; init; }
}
