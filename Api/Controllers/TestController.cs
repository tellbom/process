using FlowableWrapper.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/test")]
public sealed class TestController : ControllerBase
{
    private const int MaxCallbackRecords = 100_000;
    private static readonly ConcurrentQueue<TestCallbackRecord> CallbackRecords = new();
    private static long _recordCount;
    private static long _totalProcessCallbacks;
    private static long _fastProcessCallbacks;
    private static long _slowProcessCallbacks;
    private static long _activeProcessCallbacks;
    private static long _maxActiveProcessCallbacks;

    [HttpGet]
    public IActionResult Get() => Ok(new { ok = true });

    [AllowAnonymous]
    [HttpDelete("callbacks")]
    public IActionResult ClearCallbacks()
    {
        while (CallbackRecords.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _recordCount, 0);
        Interlocked.Exchange(ref _totalProcessCallbacks, 0);
        Interlocked.Exchange(ref _fastProcessCallbacks, 0);
        Interlocked.Exchange(ref _slowProcessCallbacks, 0);
        Interlocked.Exchange(
            ref _maxActiveProcessCallbacks,
            Interlocked.Read(ref _activeProcessCallbacks));
        return Ok(new { ok = true, count = 0 });
    }

    [AllowAnonymous]
    [HttpGet("callback-metrics")]
    public IActionResult GetCallbackMetrics()
    {
        return Ok(new
        {
            ok = true,
            retainedRecords = Interlocked.Read(ref _recordCount),
            totalProcessCallbacks = Interlocked.Read(ref _totalProcessCallbacks),
            fastProcessCallbacks = Interlocked.Read(ref _fastProcessCallbacks),
            slowProcessCallbacks = Interlocked.Read(ref _slowProcessCallbacks),
            activeProcessCallbacks = Interlocked.Read(ref _activeProcessCallbacks),
            maxActiveProcessCallbacks = Interlocked.Read(ref _maxActiveProcessCallbacks)
        });
    }

    [AllowAnonymous]
    [HttpGet("callbacks")]
    public IActionResult GetCallbacks([FromQuery] string? businessId = null)
    {
        var records = CallbackRecords.ToArray()
            .Where(r => string.IsNullOrWhiteSpace(businessId)
                || string.Equals(r.BusinessId, businessId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.ReceivedAt)
            .ToList();

        return Ok(new
        {
            ok = true,
            count = records.Count,
            records
        });
    }

    [AllowAnonymous]
    [HttpPost("node-callback")]
    public IActionResult NodeCallback([FromBody] NodeCompletedCallbackPayload request)
    {
        EnqueueRecord(TestCallbackRecord.FromNode(request));

        return Ok(new
        {
            ok = true,
            callbackType = request.CallbackType,
            received = request
        });
    }

    [AllowAnonymous]
    [HttpPost("process-callback/{group?}")]
    public async Task<IActionResult> ProcessCallback(
        [FromBody] BusinessCallbackPayload request,
        [FromRoute] string? group = null,
        [FromQuery] int delayMs = 0,
        [FromQuery] int slowPercent = 50,
        [FromQuery] int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        var resolvedGroup = ResolveCallbackGroup(
            group,
            request.BusinessId,
            slowPercent);
        var effectiveDelayMs = string.Equals(
            resolvedGroup,
            "B",
            StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(delayMs <= 0 ? 15_000 : delayMs, 1, 120_000)
            : 0;

        var active = Interlocked.Increment(ref _activeProcessCallbacks);
        UpdateMax(ref _maxActiveProcessCallbacks, active);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (effectiveDelayMs > 0)
                await Task.Delay(effectiveDelayMs, cancellationToken);

            stopwatch.Stop();
            Interlocked.Increment(ref _totalProcessCallbacks);
            if (effectiveDelayMs > 0)
                Interlocked.Increment(ref _slowProcessCallbacks);
            else
                Interlocked.Increment(ref _fastProcessCallbacks);

            EnqueueRecord(TestCallbackRecord.FromProcess(
                request,
                resolvedGroup,
                effectiveDelayMs,
                stopwatch.ElapsedMilliseconds,
                Request.Headers["X-Callback-Event-Id"].ToString(),
                Request.Headers["Idempotency-Key"].ToString()));

            var response = new
            {
                ok = statusCode is >= 200 and < 300,
                callbackType = "process_completed",
                group = resolvedGroup,
                delayMs = effectiveDelayMs,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                received = request
            };
            return StatusCode(Math.Clamp(statusCode, 100, 599), response);
        }
        finally
        {
            Interlocked.Decrement(ref _activeProcessCallbacks);
        }
    }

    private static string ResolveCallbackGroup(
        string? group,
        string? businessId,
        int slowPercent)
    {
        if (!string.Equals(group, "mixed", StringComparison.OrdinalIgnoreCase))
            return string.Equals(group, "B", StringComparison.OrdinalIgnoreCase)
                ? "B"
                : "A";

        var normalizedPercent = Math.Clamp(slowPercent, 0, 100);
        var hash = 17;
        foreach (var character in businessId ?? string.Empty)
            hash = unchecked(hash * 31 + character);

        var bucket = (int)((uint)hash % 100);
        return bucket < normalizedPercent ? "B" : "A";
    }

    private static void EnqueueRecord(TestCallbackRecord record)
    {
        CallbackRecords.Enqueue(record);
        var count = Interlocked.Increment(ref _recordCount);

        while (count > MaxCallbackRecords && CallbackRecords.TryDequeue(out _))
            count = Interlocked.Decrement(ref _recordCount);
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;

            current = observed;
        }
    }

    public sealed class TestCallbackRecord
    {
        public string Kind { get; set; } = string.Empty;
        public string? BusinessId { get; set; }
        public string? ProcessInstanceId { get; set; }
        public string? CallbackType { get; set; }
        public string? TaskDefinitionKey { get; set; }
        public string? NodeSemantic { get; set; }
        public string? Group { get; set; }
        public int DelayMs { get; set; }
        public long DurationMs { get; set; }
        public DateTime ReceivedAt { get; set; }
        public string? EventId { get; set; }
        public string? IdempotencyKey { get; set; }
        public object? Payload { get; set; }

        public static TestCallbackRecord FromNode(NodeCompletedCallbackPayload payload)
            => new()
            {
                Kind = "node",
                BusinessId = payload.BusinessId,
                ProcessInstanceId = payload.ProcessInstanceId,
                CallbackType = payload.CallbackType,
                TaskDefinitionKey = payload.TaskDefinitionKey,
                NodeSemantic = payload.NodeSemantic,
                ReceivedAt = DateTime.UtcNow,
                Payload = payload
            };

        public static TestCallbackRecord FromProcess(
            BusinessCallbackPayload payload,
            string group,
            int delayMs,
            long durationMs,
            string eventId,
            string idempotencyKey)
            => new()
            {
                Kind = "process",
                BusinessId = payload.BusinessId,
                ProcessInstanceId = payload.ProcessInstanceId,
                CallbackType = "PROCESS_COMPLETED",
                Group = group,
                DelayMs = delayMs,
                DurationMs = durationMs,
                ReceivedAt = DateTime.UtcNow,
                EventId = eventId,
                IdempotencyKey = idempotencyKey,
                Payload = payload
            };
    }
}
