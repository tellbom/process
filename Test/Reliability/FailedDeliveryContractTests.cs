using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Reliability;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

public sealed class FailedDeliveryContractTests
{
    [Fact]
    public void Dead_letter_maps_to_neutral_contract_and_exposes_retry_and_cancel()
    {
        var callbackEvent = CreateEvent(CallbackEventStatus.DeadLetter);
        var business = new WorkflowBusinessInstance
        {
            BusinessId = callbackEvent.BusinessId,
            BusinessTitle = "质量归零",
            ProcessInstanceId = callbackEvent.ProcessInstanceId,
            FlowState = "completed"
        };

        var result = FailedDeliveryAppService.Map(
            callbackEvent,
            business,
            includeFullError: true);

        Assert.Equal(FailedDeliverySources.CallbackOutbox, result.Source);
        Assert.Equal(callbackEvent.EventId, result.DeliveryId);
        Assert.Equal("https://business.example/callback", result.Target);
        Assert.Equal("business.example", result.TargetSystem);
        Assert.Equal("completed", result.ProcessState);
        Assert.Equal(
            new[]
            {
                FailedDeliveryActions.RetryDelivery,
                FailedDeliveryActions.CancelDelivery
            },
            result.AvailableActions);
    }

    [Fact]
    public void Cancelled_delivery_has_no_available_actions()
    {
        var result = FailedDeliveryAppService.Map(
            CreateEvent(CallbackEventStatus.Cancelled),
            null,
            includeFullError: true);

        Assert.Equal(CallbackEventStatus.Cancelled, result.Status);
        Assert.Equal("not_active", result.ProcessState);
        Assert.Empty(result.AvailableActions);
    }

    private static WorkflowCallbackEvent CreateEvent(string status)
        => new()
        {
            EventId = "event-1",
            IdempotencyKey = "process-end:process-1:end:process_completed",
            BusinessId = "business-1",
            ProcessInstanceId = "process-1",
            CallbackActivityId = "process_end",
            CallbackType = "process_completed",
            Payload = "{\"url\":\"https://business.example/callback\",\"headers\":{},\"body\":\"{}\"}",
            Status = status,
            AttemptCount = 5,
            LastHttpStatus = 500,
            LastError = "HTTP 500",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
