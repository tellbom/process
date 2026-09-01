using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;

namespace FlowableWrapper.Application.Services;

public sealed class FailedDeliveryAppService
{
    private readonly IFailedDeliveryProvider _provider;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<FailedDeliveryAppService> _logger;

    public FailedDeliveryAppService(
        IFailedDeliveryProvider provider,
        ICurrentUser currentUser,
        ILogger<FailedDeliveryAppService> logger)
    {
        _provider = provider;
        _currentUser = currentUser;
        _logger = logger;
    }

    public Task<FailedDeliveryPageDto> QueryAsync(FailedDeliveryQuery query)
        => _provider.QueryAsync(query);

    public async Task<FailedDeliveryDto> GetAsync(string deliveryId)
        => await _provider.GetAsync(deliveryId)
           ?? throw new ResourceNotFoundException(
               "失败投递不存在",
               "FAILED_DELIVERY_NOT_FOUND");

    public async Task<FailedDeliveryActionResultDto> RetryAsync(
        string deliveryId,
        string? reason)
    {
        var before = await GetAsync(deliveryId);
        await _provider.RetryAsync(deliveryId);
        LogAction(before, FailedDeliveryActions.RetryDelivery, "executable", reason);
        return new FailedDeliveryActionResultDto
        {
            DeliveryId = deliveryId,
            Action = FailedDeliveryActions.RetryDelivery,
            Status = "executable"
        };
    }

    public async Task<FailedDeliveryActionResultDto> TerminateProcessAsync(
        string deliveryId,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessException("reason 不能为空", "REASON_REQUIRED");
        var before = await GetAsync(deliveryId);
        await _provider.TerminateProcessAsync(deliveryId, reason.Trim());
        LogAction(before, FailedDeliveryActions.TerminateProcess, "terminated", reason.Trim());
        return new FailedDeliveryActionResultDto
        {
            DeliveryId = deliveryId,
            Action = FailedDeliveryActions.TerminateProcess,
            Status = "terminated"
        };
    }

    private void LogAction(
        FailedDeliveryDto before,
        string operation,
        string afterStatus,
        string? reason)
    {
        _logger.LogInformation(
            "FailedDeliveryAdminAction Operator={Operator} Operation={Operation} DeliveryId={DeliveryId} SourceId={SourceId} BusinessId={BusinessId} ProcessInstanceId={ProcessInstanceId} BeforeStatus={BeforeStatus} AfterStatus={AfterStatus} Reason={Reason} CreatedAt={CreatedAt}",
            _currentUser.UserId,
            operation,
            before.DeliveryId,
            before.SourceId,
            before.BusinessId,
            before.ProcessInstanceId,
            before.Status,
            afterStatus,
            reason,
            DateTime.UtcNow);
    }
}
