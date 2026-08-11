using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Reliability;
using Microsoft.Extensions.Options;
using process.Domain.DistributedLock;

namespace FlowableWrapper.Infrastructure.Dm8;

public sealed class CallbackInboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CallbackWorkerOptions _options;
    private readonly CallbackWorkerTelemetry _telemetry;
    private readonly IDistributedLockService _distributedLockService;
    private readonly ILogger<CallbackInboxWorker> _logger;
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _workerSlots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downstreamLimits =
        new(StringComparer.OrdinalIgnoreCase);

    public CallbackInboxWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<CallbackWorkerOptions> options,
        CallbackWorkerTelemetry telemetry,
        IDistributedLockService distributedLockService,
        ILogger<CallbackInboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _telemetry = telemetry;
        _distributedLockService = distributedLockService;
        _logger = logger;
        _workerSlots = new SemaphoreSlim(
            Math.Clamp(_options.WorkerCount, 1, 100));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Callback inbox worker is disabled.");
            return;
        }

        _logger.LogInformation(
            "Callback worker started. WorkerId={WorkerId}, WorkerCount={WorkerCount}, PollBatchSize={PollBatchSize}, LeaseSeconds={LeaseSeconds}, LeaseRenewIntervalSeconds={LeaseRenewIntervalSeconds}",
            _workerId,
            Math.Clamp(_options.WorkerCount, 1, 100),
            Math.Clamp(_options.PollBatchSize, 1, 100),
            Math.Clamp(_options.LeaseSeconds, 10, 600),
            Math.Clamp(_options.LeaseRenewIntervalSeconds, 1, 300));
        _telemetry.MarkInitialized();

        var inFlight = new HashSet<Task>();
        var maxInFlight = Math.Clamp(_options.PollBatchSize, 1, 100);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = inFlight
                    .Where(task => task.IsCompleted)
                    .ToList();
                foreach (var task in completed)
                {
                    inFlight.Remove(task);
                    ObserveDispatchCompletion(task);
                }

                var available = maxInFlight - inFlight.Count;
                var leasedCount = 0;
                if (available > 0)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider
                        .GetRequiredService<IWorkflowReliabilityStore>();
                    var events = await store.LeaseCallbacksAsync(
                        _workerId,
                        Math.Min(
                            available,
                            Math.Clamp(_options.PollBatchSize, 1, 100)),
                        TimeSpan.FromSeconds(Math.Clamp(
                            _options.LeaseSeconds,
                            10,
                            600)),
                        stoppingToken);
                    leasedCount = events.Count;
                    foreach (var callbackEvent in events)
                    {
                        inFlight.Add(DispatchOneScopedAsync(
                            callbackEvent,
                            stoppingToken));
                    }
                }

                if (leasedCount == 0)
                {
                    await Task.Delay(
                        Math.Clamp(
                            _options.PollIntervalMilliseconds,
                            100,
                            30000),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Callback inbox worker iteration failed.");
            }
        }

        foreach (var task in inFlight)
            ObserveDispatchCompletion(task);
    }

    internal async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IWorkflowReliabilityStore>();
        var events = await store.LeaseCallbacksAsync(
            _workerId,
            Math.Clamp(_options.PollBatchSize, 1, 100),
            TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 10, 600)),
            cancellationToken);

        await Task.WhenAll(events.Select(callbackEvent =>
            DispatchOneAsync(store, callbackEvent, cancellationToken)));
    }

    private async Task DispatchOneScopedAsync(
        WorkflowCallbackEvent callbackEvent,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IWorkflowReliabilityStore>();
        await DispatchOneAsync(store, callbackEvent, cancellationToken);
    }

    private void ObserveDispatchCompletion(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown or after lease loss.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Callback dispatch task failed unexpectedly.");
        }
    }

    private async Task DispatchOneAsync(
        IWorkflowReliabilityStore store,
        WorkflowCallbackEvent callbackEvent,
        CancellationToken cancellationToken)
    {
        CallbackDispatchEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CallbackDispatchEnvelope>(
                callbackEvent.Payload,
                JsonOptions) ?? throw new JsonException("Callback envelope is empty.");
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(
                store,
                callbackEvent,
                null,
                exception.Message,
                cancellationToken,
                forceDeadLetter: true);
            return;
        }

        if (!Uri.TryCreate(envelope.Url, UriKind.Absolute, out var uri))
        {
            await MarkFailedAsync(
                store,
                callbackEvent,
                null,
                "Callback URL is invalid.",
                cancellationToken,
                forceDeadLetter: true);
            return;
        }

        var downstreamKey = uri.GetLeftPart(UriPartial.Authority);
        var downstreamLimit = _downstreamLimits.GetOrAdd(
            downstreamKey,
            _ => new SemaphoreSlim(
                Math.Clamp(_options.PerDownstreamConcurrency, 1, 100)));

        using var leaseCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewLeaseUntilCancelledAsync(
            store,
            callbackEvent,
            leaseCancellation);

        var dispatchStarted = 0L;
        var succeeded = false;
        var downstreamAcquired = false;
        var workerAcquired = false;
        var telemetryStarted = false;
        DownstreamSlotLease? distributedSlot = null;
        try
        {
            await downstreamLimit.WaitAsync(leaseCancellation.Token);
            downstreamAcquired = true;
            distributedSlot = await AcquireDownstreamSlotAsync(
                downstreamKey,
                leaseCancellation.Token);
            await _workerSlots.WaitAsync(leaseCancellation.Token);
            workerAcquired = true;
            dispatchStarted = Stopwatch.GetTimestamp();
            _telemetry.DispatchStarted(downstreamKey);
            telemetryStarted = true;

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            foreach (var header in envelope.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.TryAddWithoutValidation(
                "X-Callback-Event-Id",
                callbackEvent.EventId);
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                callbackEvent.IdempotencyKey);
            request.Content = new StringContent(
                envelope.Body,
                Encoding.UTF8,
                "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                leaseCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(_options.HttpTimeoutSeconds, 1, 300)));

            try
            {
                var client = _httpClientFactory.CreateClient("BusinessCallbackWorker");
                var sendTask = client.SendAsync(request, timeout.Token);
                var completed = await Task.WhenAny(sendTask, renewalTask);
                if (completed == renewalTask && !await renewalTask)
                {
                    timeout.Cancel();
                    try
                    {
                        using var ignored = await sendTask;
                    }
                    catch
                    {
                        // The request is deliberately cancelled after losing
                        // the DM8 lease. A new owner decides the final state.
                    }

                    _logger.LogWarning(
                        "Callback dispatch abandoned after DM8 lease loss. EventId={EventId}, WorkerId={WorkerId}, Downstream={Downstream}",
                        callbackEvent.EventId,
                        _workerId,
                        downstreamKey);
                    return;
                }

                using var response = await sendTask;
                leaseCancellation.Cancel();
                await ObserveRenewalCompletionAsync(renewalTask);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    await store.MarkCallbackSucceededAsync(
                        callbackEvent.EventId,
                        _workerId,
                        status,
                        cancellationToken);
                    if (IsProcessEndCallback(callbackEvent))
                    {
                        await store.MarkBusinessCallbackStateAsync(
                            callbackEvent.BusinessId,
                            "succeeded",
                            flowCompleted: true,
                            cancellationToken: cancellationToken);
                    }
                    succeeded = true;
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                await MarkFailedAsync(
                    store,
                    callbackEvent,
                    status,
                    $"HTTP {status}: {responseBody}",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (renewalTask.IsCompleted && !await renewalTask)
                    return;

                await MarkFailedAsync(
                    store,
                    callbackEvent,
                    (int)HttpStatusCode.RequestTimeout,
                    "Business callback timed out.",
                    cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                await MarkFailedAsync(
                    store,
                    callbackEvent,
                    null,
                    exception.Message,
                    cancellationToken);
            }
        }
        finally
        {
            leaseCancellation.Cancel();
            await ObserveRenewalCompletionAsync(renewalTask);
            if (telemetryStarted)
            {
                _telemetry.DispatchCompleted(
                    downstreamKey,
                    succeeded,
                    TimeSpan.FromSeconds(
                        (Stopwatch.GetTimestamp() - dispatchStarted)
                        / (double)Stopwatch.Frequency));
            }
            if (workerAcquired)
                _workerSlots.Release();
            if (distributedSlot != null)
            {
                try
                {
                    await _distributedLockService.ReleaseAsync(
                        distributedSlot.Key,
                        distributedSlot.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to release Redis downstream concurrency slot. Downstream={Downstream}, Slot={Slot}",
                        downstreamKey,
                        distributedSlot.Key);
                }
            }
            if (downstreamAcquired)
                downstreamLimit.Release();
        }
    }

    private async Task<DownstreamSlotLease?> AcquireDownstreamSlotAsync(
        string downstream,
        CancellationToken cancellationToken)
    {
        var slotCount = Math.Clamp(
            _options.PerDownstreamConcurrency,
            1,
            100);
        var downstreamHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    downstream.ToLowerInvariant())))
            .ToLowerInvariant();
        var expiry = TimeSpan.FromSeconds(Math.Max(
            Math.Clamp(_options.LeaseSeconds, 10, 600),
            Math.Clamp(_options.HttpTimeoutSeconds, 1, 300) + 30));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (var slot = 0; slot < slotCount; slot++)
                {
                    var key =
                        $"callback:downstream:{downstreamHash}:{slot}";
                    var token = Guid.NewGuid().ToString("N");
                    if (await _distributedLockService.TryAcquireAsync(
                            key,
                            token,
                            expiry))
                    {
                        return new DownstreamSlotLease(key, token);
                    }
                }

                await Task.Delay(
                    Math.Clamp(_options.PollIntervalMilliseconds, 100, 1000),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Redis downstream concurrency coordination is unavailable; DM8 lease remains authoritative. Downstream={Downstream}",
                downstream);
            return null;
        }

        return null;
    }

    private async Task<bool> RenewLeaseUntilCancelledAsync(
        IWorkflowReliabilityStore store,
        WorkflowCallbackEvent callbackEvent,
        CancellationTokenSource leaseCancellation)
    {
        var cancellationToken = leaseCancellation.Token;
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            _options.LeaseRenewIntervalSeconds,
            1,
            Math.Max(1, Math.Clamp(_options.LeaseSeconds, 10, 600) - 1)));
        var duration = TimeSpan.FromSeconds(
            Math.Clamp(_options.LeaseSeconds, 10, 600));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                if (!await store.RenewCallbackLeaseAsync(
                        callbackEvent.EventId,
                        _workerId,
                        duration,
                        cancellationToken))
                {
                    _telemetry.LeaseLost();
                    leaseCancellation.Cancel();
                    return false;
                }

                _logger.LogDebug(
                    "Callback lease renewed. EventId={EventId}, WorkerId={WorkerId}",
                    callbackEvent.EventId,
                    _workerId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        return true;
    }

    private static async Task ObserveRenewalCompletionAsync(
        Task<bool> renewalTask)
    {
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when a dispatch finishes or the process is stopping.
        }
    }

    private async Task MarkFailedAsync(
        IWorkflowReliabilityStore store,
        WorkflowCallbackEvent callbackEvent,
        int? httpStatus,
        string error,
        CancellationToken cancellationToken,
        bool forceDeadLetter = false)
    {
        var attempt = callbackEvent.AttemptCount + 1;
        var decision = forceDeadLetter
            ? new CallbackRetryDecision(CallbackEventStatus.DeadLetter, null)
            : CallbackRetryPolicy.Decide(
                attempt,
                Math.Clamp(_options.MaxRetryCount, 1, 20),
                httpStatus,
                DateTime.Now,
                Math.Clamp(_options.RetryBaseSeconds, 1, 300),
                Math.Clamp(_options.RetryMaxSeconds, 1, 86400),
                Math.Clamp(_options.RetryJitterPercent, 0, 50));
        await store.MarkCallbackFailedAsync(
            callbackEvent.EventId,
            _workerId,
            decision,
            httpStatus,
            error,
            cancellationToken);
        if (IsProcessEndCallback(callbackEvent))
        {
            await store.MarkBusinessCallbackStateAsync(
                callbackEvent.BusinessId,
                decision.Status == CallbackEventStatus.DeadLetter
                    ? "dead_letter"
                    : "retry_waiting",
                flowCompleted: true,
                cancellationToken: cancellationToken);
        }
    }

    private static bool IsProcessEndCallback(
        WorkflowCallbackEvent callbackEvent)
        => string.Equals(
            callbackEvent.CallbackType,
            "process_completed",
            StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record DownstreamSlotLease(string Key, string Token);
}
