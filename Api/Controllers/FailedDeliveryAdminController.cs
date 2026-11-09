using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Infrastructure.Dm8;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/admin/failed-deliveries")]
public sealed class FailedDeliveryAdminController : ControllerBase
{
    private readonly FailedDeliveryAppService _service;
    private readonly IWorkflowReliabilityStore _store;
    private readonly CallbackWorkerTelemetry _telemetry;

    public FailedDeliveryAdminController(
        FailedDeliveryAppService service,
        IWorkflowReliabilityStore store,
        CallbackWorkerTelemetry telemetry)
    {
        _service = service;
        _store = store;
        _telemetry = telemetry;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResult<FailedDeliveryPageDto>>> Query(
        [FromQuery] FailedDeliveryQuery query,
        CancellationToken cancellationToken)
        => Ok(ApiResult<FailedDeliveryPageDto>.Ok(
            await _service.QueryAsync(query, cancellationToken)));

    [HttpGet("{deliveryId}")]
    public async Task<ActionResult<ApiResult<FailedDeliveryDto>>> Get(
        string deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await _service.GetAsync(deliveryId, cancellationToken);
        return delivery == null
            ? NotFound()
            : Ok(ApiResult<FailedDeliveryDto>.Ok(delivery));
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken cancellationToken)
    {
        var queue = await _store.GetCallbackQueueSnapshotAsync(cancellationToken);
        var worker = _telemetry.Snapshot();
        return Ok(ApiResult<object>.Ok(new
        {
            queue.Pending,
            queue.Processing,
            queue.RetryWaiting,
            queue.Succeeded,
            queue.DeadLetter,
            queue.Cancelled,
            queue.OldestPendingCreatedAt,
            oldestPendingAgeSeconds = queue.OldestPendingAgeSeconds(DateTime.Now),
            worker.SuccessPerMinute,
            worker.FailurePerMinute,
            worker.LeaseLosses,
            worker.Downstreams
        }));
    }

    [HttpPost("{deliveryId}/retry")]
    public async Task<IActionResult> Retry(
        string deliveryId,
        [FromBody] FailedDeliveryActionRequest? request,
        CancellationToken cancellationToken)
    {
        var callbackEvent = await _store.GetCallbackByEventIdAsync(
            deliveryId,
            cancellationToken);
        if (callbackEvent == null)
            return NotFound();
        if (callbackEvent.Status != CallbackEventStatus.DeadLetter)
            return StateConflict(deliveryId, callbackEvent.Status);

        var action = await PrepareActionAsync(
            callbackEvent,
            "callback_manual_retry",
            request?.Reason,
            cancellationToken);
        var applied = await _store.RetryDeadLetterAsync(
            deliveryId,
            cancellationToken);
        await CompleteActionAsync(action, applied, "retry_scheduled", cancellationToken);
        if (!applied)
            return StateConflict(deliveryId, callbackEvent.Status);

        await UpdateProcessEndCallbackStateAsync(
            callbackEvent,
            CallbackEventStatus.Pending,
            cancellationToken);
        return Accepted(ApiResult<FailedDeliveryActionResultDto>.Ok(new()
        {
            DeliveryId = deliveryId,
            Action = FailedDeliveryActions.RetryDelivery,
            Status = CallbackEventStatus.Pending
        }));
    }

    [HttpPost("{deliveryId}/cancel")]
    public async Task<IActionResult> Cancel(
        string deliveryId,
        [FromBody] FailedDeliveryActionRequest? request,
        CancellationToken cancellationToken)
    {
        var callbackEvent = await _store.GetCallbackByEventIdAsync(
            deliveryId,
            cancellationToken);
        if (callbackEvent == null)
            return NotFound();
        if (callbackEvent.Status != CallbackEventStatus.DeadLetter)
            return StateConflict(deliveryId, callbackEvent.Status);

        var action = await PrepareActionAsync(
            callbackEvent,
            "callback_manual_cancel",
            request?.Reason,
            cancellationToken);
        var applied = await _store.CancelDeadLetterAsync(
            deliveryId,
            cancellationToken);
        await CompleteActionAsync(action, applied, "delivery_cancelled", cancellationToken);
        if (!applied)
            return StateConflict(deliveryId, callbackEvent.Status);

        await UpdateProcessEndCallbackStateAsync(
            callbackEvent,
            CallbackEventStatus.Cancelled,
            cancellationToken);
        return Ok(ApiResult<FailedDeliveryActionResultDto>.Ok(new()
        {
            DeliveryId = deliveryId,
            Action = FailedDeliveryActions.CancelDelivery,
            Status = CallbackEventStatus.Cancelled
        }));
    }

    private async Task<WorkflowTaskAction> PrepareActionAsync(
        WorkflowCallbackEvent callbackEvent,
        string actionType,
        string? reason,
        CancellationToken cancellationToken)
    {
        var operatorId = User.FindFirst("userid")?.Value
                         ?? User.FindFirst("sub")?.Value
                         ?? User.Identity?.Name
                         ?? "authenticated-user";
        var idempotencyKey = $"{actionType}:{callbackEvent.EventId}:{callbackEvent.RowVersion}";
        return await _store.PrepareTaskActionAsync(
            new PrepareTaskActionCommand
            {
                ActionId = StableId(idempotencyKey),
                IdempotencyKey = idempotencyKey,
                BusinessId = callbackEvent.BusinessId,
                ProcessInstanceId = callbackEvent.ProcessInstanceId,
                ActionType = actionType,
                OperatorId = operatorId,
                RequestJson = JsonSerializer.Serialize(new
                {
                    deliveryId = callbackEvent.EventId,
                    reason
                })
            },
            cancellationToken);
    }

    private async Task CompleteActionAsync(
        WorkflowTaskAction action,
        bool applied,
        string successResult,
        CancellationToken cancellationToken)
        => await _store.MarkTaskActionResultAsync(
            action.ActionId,
            applied ? "applied" : "failed",
            applied ? successResult : "state_conflict",
            applied ? null : "Delivery state changed concurrently.",
            cancellationToken);

    private async Task UpdateProcessEndCallbackStateAsync(
        WorkflowCallbackEvent callbackEvent,
        string callbackState,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                callbackEvent.CallbackType,
                "process_completed",
                StringComparison.OrdinalIgnoreCase))
            return;

        await _store.MarkBusinessCallbackStateAsync(
            callbackEvent.ProcessInstanceId,
            callbackState,
            flowCompleted: false,
            cancellationToken);
    }

    private ObjectResult StateConflict(string deliveryId, string status)
        => Conflict(ApiResult<object>.Fail(
            $"Delivery {deliveryId} is in state '{status}'; only dead_letter can be operated."));

    private static string StableId(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
