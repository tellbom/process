using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Domain.Reliability;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/admin/callback-events")]
public sealed class CallbackAdminController : ControllerBase
{
    private readonly IWorkflowReliabilityStore _store;

    public CallbackAdminController(IWorkflowReliabilityStore store)
        => _store = store;

    [HttpGet("{eventId}")]
    public async Task<ActionResult<CallbackEventAdminDto>> Get(
        string eventId,
        CancellationToken cancellationToken)
    {
        var callbackEvent = await _store.GetCallbackByEventIdAsync(
            eventId,
            cancellationToken);
        return callbackEvent == null
            ? NotFound()
            : Ok(Map(callbackEvent));
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string? businessId,
        [FromQuery] string? processInstanceId,
        [FromQuery] string? status,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await _store.QueryCallbacksAsync(
            businessId,
            processInstanceId,
            status,
            (pageIndex - 1) * pageSize,
            pageSize,
            cancellationToken);
        return Ok(new
        {
            items = result.Items.Select(Map),
            result.Total,
            pageIndex,
            pageSize
        });
    }

    [HttpPost("{eventId}/retry")]
    public async Task<IActionResult> Retry(
        string eventId,
        [FromBody] RetryCallbackRequest? request,
        CancellationToken cancellationToken)
    {
        var callbackEvent = await _store.GetCallbackByEventIdAsync(
            eventId, cancellationToken);
        if (callbackEvent == null)
            return NotFound();
        if (callbackEvent.Status != CallbackEventStatus.DeadLetter)
            return Conflict(new
            {
                message = "Only dead-letter events can be retried."
            });

        var operatorId = User.FindFirst("userid")?.Value
                         ?? User.FindFirst("sub")?.Value
                         ?? User.Identity?.Name
                         ?? "authenticated-user";
        var idempotencyKey =
            $"callback-manual-retry:{eventId}:{callbackEvent.AttemptCount}";
        var action = await _store.PrepareTaskActionAsync(
            new PrepareTaskActionCommand
            {
                ActionId = StableId(idempotencyKey),
                IdempotencyKey = idempotencyKey,
                BusinessId = callbackEvent.BusinessId,
                ProcessInstanceId = callbackEvent.ProcessInstanceId,
                ActionType = "callback_manual_retry",
                OperatorId = operatorId,
                RequestJson = JsonSerializer.Serialize(new
                {
                    eventId,
                    reason = request?.Reason
                })
            },
            cancellationToken);

        var retried = await _store.RetryDeadLetterAsync(
            eventId, cancellationToken);
        await _store.MarkTaskActionResultAsync(
            action.ActionId,
            retried ? "applied" : "failed",
            retried ? "retry_scheduled" : "state_conflict",
            retried ? null : "Callback state changed concurrently.",
            cancellationToken);
        return retried
            ? Accepted(new { eventId, status = CallbackEventStatus.Pending })
            : Conflict(new
            {
                message = "Callback state changed concurrently."
            });
    }

    private static CallbackEventAdminDto Map(WorkflowCallbackEvent value)
        => new()
        {
            EventId = value.EventId,
            BusinessId = value.BusinessId,
            ProcessInstanceId = value.ProcessInstanceId,
            CallbackActivityId = value.CallbackActivityId,
            CallbackType = value.CallbackType,
            Status = value.Status,
            AttemptCount = value.AttemptCount,
            NextAttemptAt = value.NextAttemptAt,
            LastHttpStatus = value.LastHttpStatus,
            LastError = Truncate(value.LastError, 500),
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt,
            CompletedAt = value.CompletedAt
        };

    private static string StableId(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}

public sealed class RetryCallbackRequest
{
    public string? Reason { get; set; }
}

public sealed class CallbackEventAdminDto
{
    public string EventId { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string CallbackActivityId { get; set; } = string.Empty;
    public string CallbackType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
