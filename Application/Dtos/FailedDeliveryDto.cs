namespace FlowableWrapper.Application.Dtos;

public static class FailedDeliverySources
{
    public const string FlowableAsyncJob = "flowable_async_job";
}

public static class FailedDeliveryStatuses
{
    public const string DeadLetter = "dead_letter";
}

public static class FailedDeliveryTypes
{
    public const string ProcessCompleted = "process_completed";
}

public static class FailedDeliveryActions
{
    public const string RetryDelivery = "retry_delivery";
    public const string TerminateProcess = "terminate_process";
}

public sealed class FailedDeliveryDto
{
    public string DeliveryId { get; set; } = string.Empty;
    public string Source { get; set; } = FailedDeliverySources.FlowableAsyncJob;
    public string SourceId { get; set; } = string.Empty;
    public string DeliveryType { get; set; } = FailedDeliveryTypes.ProcessCompleted;
    public string? BusinessId { get; set; }
    public string ProcessInstanceId { get; set; } = string.Empty;
    public string ProcessState { get; set; } = string.Empty;
    public string ActivityId { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string Status { get; set; } = FailedDeliveryStatuses.DeadLetter;
    public int? AttemptCount { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastFailedAt { get; set; }
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
