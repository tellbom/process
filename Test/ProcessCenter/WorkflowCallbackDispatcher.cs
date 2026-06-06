namespace FlowableWrapper.Test.ProcessCenter;

public interface IWorkflowCallbackHandler
{
    Task OnNodeCompletedAsync(
        NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken);

    Task OnRejectOccurredAsync(
        NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken);

    Task OnProcessCompletedAsync(
        BusinessCallbackPayload payload,
        CancellationToken cancellationToken);
}

public sealed class WorkflowCallbackDispatcher
{
    private readonly IWorkflowCallbackHandler _handler;

    public WorkflowCallbackDispatcher(IWorkflowCallbackHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public async Task<CallbackHandleResult> HandleNodeCallbackAsync(
        NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        if (string.Equals(payload.CallbackType, WorkflowCallbackTypes.RejectOccurred,
                StringComparison.OrdinalIgnoreCase))
        {
            await _handler.OnRejectOccurredAsync(payload, cancellationToken);
            return CallbackHandleResult.Ok("Reject callback handled.");
        }

        await _handler.OnNodeCompletedAsync(payload, cancellationToken);
        return CallbackHandleResult.Ok("Node callback handled.");
    }

    public async Task<CallbackHandleResult> HandleProcessCompletedCallbackAsync(
        BusinessCallbackPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        await _handler.OnProcessCompletedAsync(payload, cancellationToken);
        return CallbackHandleResult.Ok("Process completed callback handled.");
    }
}
