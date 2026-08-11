using FlowableWrapper.Configuration;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Reliability;

public sealed class OperationConcurrencyGate
{
    private readonly SemaphoreSlim _total;
    private readonly SemaphoreSlim _processStart;
    private readonly SemaphoreSlim _taskComplete;
    private readonly SemaphoreSlim _pendingQuery;

    public OperationConcurrencyGate(IOptions<OperationConcurrencyOptions> options)
    {
        var value = options.Value;
        _total = Create(value.Total);
        _processStart = Create(value.ProcessStart);
        _taskComplete = Create(value.TaskComplete);
        _pendingQuery = Create(value.PendingQuery);
    }

    public Task<IDisposable> EnterProcessStartAsync(CancellationToken cancellationToken)
        => EnterAsync(_total, _processStart, cancellationToken);

    public Task<IDisposable> EnterTaskCompleteAsync(CancellationToken cancellationToken)
        => EnterAsync(_total, _taskComplete, cancellationToken);

    public Task<IDisposable> EnterPendingQueryAsync(CancellationToken cancellationToken)
        => EnterAsync(_total, _pendingQuery, cancellationToken);

    private static SemaphoreSlim Create(int concurrency)
    {
        var bounded = Math.Clamp(concurrency, 1, 1000);
        return new SemaphoreSlim(bounded, bounded);
    }

    private static async Task<IDisposable> EnterAsync(
        SemaphoreSlim total,
        SemaphoreSlim operation,
        CancellationToken cancellationToken)
    {
        await total.WaitAsync(cancellationToken);
        try
        {
            await operation.WaitAsync(cancellationToken);
            return new Lease(total, operation);
        }
        catch
        {
            total.Release();
            throw;
        }
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _total;
        private SemaphoreSlim? _operation;

        public Lease(SemaphoreSlim total, SemaphoreSlim operation)
        {
            _total = total;
            _operation = operation;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _operation, null)?.Release();
            Interlocked.Exchange(ref _total, null)?.Release();
        }
    }
}
