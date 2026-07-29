namespace FlowableWrapper.Domain.Reliability;

public sealed class WorkflowBusinessInstance
{
    public long Id { get; init; }
    public string BusinessId { get; init; } = string.Empty;
    public string BusinessType { get; init; } = string.Empty;
    public string? ProcessInstanceId { get; init; }
    public string ProcessDefinitionKey { get; init; } = string.Empty;
    public int? ProcessDefinitionVersion { get; init; }
    public string FlowState { get; init; } = string.Empty;
    public string CallbackState { get; init; } = string.Empty;
    public string? CallbackConfigSnapshot { get; init; }
    public string? RecommendedAssigneesSnapshot { get; init; }
    public long RowVersion { get; init; }
    public long DataVersion { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed class WorkflowCallbackEvent
{
    public string EventId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string BusinessId { get; init; } = string.Empty;
    public string ProcessInstanceId { get; init; } = string.Empty;
    public string CallbackActivityId { get; init; } = string.Empty;
    public string CallbackType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string Status { get; init; } = CallbackEventStatus.Pending;
    public int AttemptCount { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTime? LeaseUntil { get; init; }
    public int? LastHttpStatus { get; init; }
    public string? LastError { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long RowVersion { get; init; }
}

public sealed class EnqueueCallbackCommand
{
    public string EventId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string BusinessId { get; init; } = string.Empty;
    public string ProcessInstanceId { get; init; } = string.Empty;
    public string CallbackActivityId { get; init; } = string.Empty;
    public string CallbackType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public bool CompleteBusinessFlow { get; init; }
}

public sealed class ReserveBusinessCommand
{
    public string BusinessId { get; init; } = string.Empty;
    public string BusinessType { get; init; } = string.Empty;
    public string ProcessDefinitionKey { get; init; } = string.Empty;
    public string? CallbackConfigSnapshot { get; init; }
}

public sealed record BusinessReservation(
    bool Created,
    WorkflowBusinessInstance Instance);

public sealed class CallbackDispatchEnvelope
{
    public string Url { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new();
    public string Body { get; init; } = string.Empty;
}

public sealed class WorkflowDefinitionConfig
{
    public string ProcessDefinitionKey { get; init; } = string.Empty;
    public int ProcessDefinitionVersion { get; init; }
    public string ProcessDefinitionId { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string ConfigJson { get; init; } = string.Empty;
    public long DataVersion { get; init; }
}

public sealed class WorkflowTaskAction
{
    public string ActionId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string BusinessId { get; init; } = string.Empty;
    public string ProcessInstanceId { get; init; } = string.Empty;
    public string? TaskId { get; init; }
    public string? TaskDefinitionKey { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string OperatorId { get; init; } = string.Empty;
    public string? RequestJson { get; init; }
    public string ResultState { get; init; } = "prepared";
    public string? FlowableResult { get; init; }
    public string? LastError { get; init; }
    public long DataVersion { get; init; }
}

public sealed class PrepareTaskActionCommand
{
    public string ActionId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string BusinessId { get; init; } = string.Empty;
    public string ProcessInstanceId { get; init; } = string.Empty;
    public string? TaskId { get; init; }
    public string? TaskDefinitionKey { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string OperatorId { get; init; } = string.Empty;
    public string? RequestJson { get; init; }
}

public interface IWorkflowReliabilityStore
{
    Task<BusinessReservation> ReserveBusinessAsync(
        ReserveBusinessCommand command,
        CancellationToken cancellationToken = default);

    Task BindStartedProcessAsync(
        string businessId,
        string processInstanceId,
        int? processDefinitionVersion,
        CancellationToken cancellationToken = default);

    Task MarkBusinessFlowStateAsync(
        string businessId,
        string flowState,
        CancellationToken cancellationToken = default);

    Task UpdateRecommendedAssigneesSnapshotAsync(
        string businessId,
        string snapshotJson,
        CancellationToken cancellationToken = default);

    Task MarkBusinessCallbackStateAsync(
        string businessId,
        string callbackState,
        bool flowCompleted,
        CancellationToken cancellationToken = default);

    Task<WorkflowBusinessInstance?> GetBusinessByBusinessIdAsync(
        string businessId,
        CancellationToken cancellationToken = default);

    Task<WorkflowBusinessInstance?> GetBusinessByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, WorkflowBusinessInstance>>
        GetBusinessesByProcessInstancesAsync(
            IReadOnlyCollection<string> processInstanceIds,
            CancellationToken cancellationToken = default);

    Task SaveDefinitionConfigAsync(
        WorkflowDefinitionConfig config,
        CancellationToken cancellationToken = default);

    Task<WorkflowDefinitionConfig?> GetDefinitionConfigAsync(
        string processDefinitionKey,
        int processDefinitionVersion,
        CancellationToken cancellationToken = default);

    Task<WorkflowTaskAction> PrepareTaskActionAsync(
        PrepareTaskActionCommand command,
        CancellationToken cancellationToken = default);

    Task MarkTaskActionResultAsync(
        string actionId,
        string resultState,
        string flowableResult,
        string? error,
        CancellationToken cancellationToken = default);

    Task<WorkflowCallbackEvent> EnqueueCallbackAsync(
        EnqueueCallbackCommand command,
        CancellationToken cancellationToken = default);

    Task<WorkflowCallbackEvent?> GetCallbackByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<WorkflowCallbackEvent?> GetCallbackByEventIdAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WorkflowCallbackEvent> Items, int Total)>
        QueryCallbacksAsync(
            string? businessId,
            string? processInstanceId,
            string? status,
            int start,
            int size,
            CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowCallbackEvent>> LeaseCallbacksAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkCallbackSucceededAsync(
        string eventId,
        string workerId,
        int httpStatus,
        CancellationToken cancellationToken = default);

    Task MarkCallbackFailedAsync(
        string eventId,
        string workerId,
        CallbackRetryDecision decision,
        int? httpStatus,
        string error,
        CancellationToken cancellationToken = default);

    Task<bool> RetryDeadLetterAsync(
        string eventId,
        CancellationToken cancellationToken = default);
}
