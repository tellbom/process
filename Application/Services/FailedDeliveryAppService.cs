using System.Text.Json;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Reliability;

namespace FlowableWrapper.Application.Services;

public sealed class FailedDeliveryAppService
{
    private readonly IWorkflowReliabilityStore _store;

    public FailedDeliveryAppService(IWorkflowReliabilityStore store)
    {
        _store = store;
    }

    public async Task<FailedDeliveryPageDto> QueryAsync(
        FailedDeliveryQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageIndex = Math.Max(1, query.PageIndex);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        if (!string.IsNullOrWhiteSpace(query.Source)
            && !string.Equals(
                query.Source,
                FailedDeliverySources.CallbackOutbox,
                StringComparison.OrdinalIgnoreCase))
        {
            return new FailedDeliveryPageDto
            {
                PageIndex = pageIndex,
                PageSize = pageSize
            };
        }

        var result = await _store.QueryCallbacksAsync(
            query.BusinessId,
            query.ProcessInstanceId,
            query.Status,
            query.DeliveryType,
            (pageIndex - 1) * pageSize,
            pageSize,
            cancellationToken);
        var businesses = await _store.GetBusinessesByProcessInstancesAsync(
            result.Items.Select(item => item.ProcessInstanceId).ToArray(),
            cancellationToken);

        return new FailedDeliveryPageDto
        {
            Items = result.Items.Select(item => Map(
                item,
                businesses.GetValueOrDefault(item.ProcessInstanceId),
                includeFullError: false)).ToArray(),
            Total = result.Total,
            PageIndex = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<FailedDeliveryDto?> GetAsync(
        string deliveryId,
        CancellationToken cancellationToken = default)
    {
        var callbackEvent = await _store.GetCallbackByEventIdAsync(
            deliveryId,
            cancellationToken);
        if (callbackEvent == null)
            return null;

        var business = await _store.GetBusinessByProcessInstanceAsync(
            callbackEvent.ProcessInstanceId,
            cancellationToken);
        return Map(callbackEvent, business, includeFullError: true);
    }

    public static FailedDeliveryDto Map(
        WorkflowCallbackEvent callbackEvent,
        WorkflowBusinessInstance? business,
        bool includeFullError)
    {
        var target = ReadTarget(callbackEvent.Payload);
        return new FailedDeliveryDto
        {
            DeliveryId = callbackEvent.EventId,
            SourceId = callbackEvent.EventId,
            DeliveryType = callbackEvent.CallbackType,
            BusinessId = callbackEvent.BusinessId,
            BusinessTitle = business?.BusinessTitle,
            ProcessInstanceId = callbackEvent.ProcessInstanceId,
            ProcessState = business?.FlowState ?? "not_active",
            ActivityId = callbackEvent.CallbackActivityId,
            Target = target,
            TargetSystem = ReadTargetSystem(target),
            Status = callbackEvent.Status,
            AttemptCount = callbackEvent.AttemptCount,
            NextAttemptAt = callbackEvent.NextAttemptAt,
            LastHttpStatus = callbackEvent.LastHttpStatus,
            LastError = includeFullError
                ? callbackEvent.LastError
                : Truncate(callbackEvent.LastError, 500),
            CreatedAt = callbackEvent.CreatedAt,
            UpdatedAt = callbackEvent.UpdatedAt,
            CompletedAt = callbackEvent.CompletedAt,
            AvailableActions = callbackEvent.Status == CallbackEventStatus.DeadLetter
                ? new List<string>
                {
                    FailedDeliveryActions.RetryDelivery,
                    FailedDeliveryActions.CancelDelivery
                }
                : new List<string>()
        };
    }

    private static string? ReadTarget(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CallbackDispatchEnvelope>(
                payload,
                JsonOptions)?.Url;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadTargetSystem(string? target)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri)
            ? uri.Authority
            : null;

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
