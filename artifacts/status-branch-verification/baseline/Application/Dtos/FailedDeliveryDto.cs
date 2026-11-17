namespace FlowableWrapper.Application.Dtos;

public static class FailedDeliverySources
{
    public const string CallbackOutbox = "callback_outbox";
}

public static class FailedDeliveryActions
{
    public const string RetryDelivery = "retry_delivery";
    public const string CancelDelivery = "cancel_delivery";
}

public sealed class FailedDeliveryDto
{
    public string DeliveryId { get; set; } = string.Empty;
    public string Source { get; set; } = FailedDeliverySources.CallbackOutbox;
    public string SourceId { get; set; } = string.Empty;
    public string DeliveryType { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string? BusinessTitle { get; set; }
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string ProcessState { get; set; } = string.Empty;
    public string ActivityId { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? TargetSystem { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<string> AvailableActions { get; set; } = new();
}

public sealed class FailedDeliveryQuery
{
    public string? BusinessId { get; set; }
    public string? ProcessInstanceId { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public string? DeliveryType { get; set; }
    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class FailedDeliveryPageDto
{
    public IReadOnlyList<FailedDeliveryDto> Items { get; set; }
        = Array.Empty<FailedDeliveryDto>();
    public int Total { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
}

public sealed class FailedDeliveryActionRequest
{
    public string? Reason { get; set; }
}

public sealed class FailedDeliveryActionResultDto
{
    public string DeliveryId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
