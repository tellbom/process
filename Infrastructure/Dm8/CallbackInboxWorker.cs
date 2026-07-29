using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Reliability;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Dm8;

public sealed class CallbackInboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CallbackWorkerOptions _options;
    private readonly ILogger<CallbackInboxWorker> _logger;
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downstreamLimits =
        new(StringComparer.OrdinalIgnoreCase);

    public CallbackInboxWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<CallbackWorkerOptions> options,
        ILogger<CallbackInboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Callback inbox worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Callback inbox worker iteration failed.");
            }

            await Task.Delay(
                Math.Clamp(_options.PollIntervalMilliseconds, 100, 30000),
                stoppingToken);
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IWorkflowReliabilityStore>();
        var concurrency = Math.Clamp(_options.GlobalConcurrency, 1, 100);
        var events = await store.LeaseCallbacksAsync(
            _workerId,
            Math.Min(Math.Clamp(_options.BatchSize, 1, 100), concurrency),
            TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 10, 600)),
            cancellationToken);

        await Task.WhenAll(events.Select(callbackEvent =>
            DispatchOneAsync(store, callbackEvent, cancellationToken)));
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
        await downstreamLimit.WaitAsync(cancellationToken);
        try
        {
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
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(_options.HttpTimeoutSeconds, 1, 300)));

            try
            {
                var client = _httpClientFactory.CreateClient("BusinessCallbackWorker");
                using var response = await client.SendAsync(request, timeout.Token);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    await store.MarkCallbackSucceededAsync(
                        callbackEvent.EventId,
                        _workerId,
                        status,
                        cancellationToken);
                    await store.MarkBusinessCallbackStateAsync(
                        callbackEvent.BusinessId,
                        "succeeded",
                        flowCompleted: true,
                        cancellationToken: cancellationToken);
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
            downstreamLimit.Release();
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
                Math.Clamp(_options.MaxAttempts, 1, 20),
                httpStatus,
                DateTime.Now);
        await store.MarkCallbackFailedAsync(
            callbackEvent.EventId,
            _workerId,
            decision,
            httpStatus,
            error,
            cancellationToken);
        await store.MarkBusinessCallbackStateAsync(
            callbackEvent.BusinessId,
            decision.Status == CallbackEventStatus.DeadLetter
                ? "dead_letter"
                : "retry_waiting",
            flowCompleted: true,
            cancellationToken: cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
