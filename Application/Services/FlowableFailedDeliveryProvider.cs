using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Flowable;
using process.Domain.Abstractions;

namespace FlowableWrapper.Application.Services;

public interface IFailedDeliveryProvider
{
    Task<FailedDeliveryPageDto> QueryAsync(FailedDeliveryQuery query);
    Task<FailedDeliveryDto?> GetAsync(string deliveryId);
    Task RetryAsync(string deliveryId);
    Task TerminateProcessAsync(string deliveryId, string reason);
}

public sealed class FlowableFailedDeliveryProvider : IFailedDeliveryProvider
{
    private const int FlowablePageSize = 100;
    private readonly IFlowableManagementService _management;
    private readonly IFlowableRuntimeService _runtime;
    private readonly IElasticSearchService _elasticSearch;

    public FlowableFailedDeliveryProvider(
        IFlowableManagementService management,
        IFlowableRuntimeService runtime,
        IElasticSearchService elasticSearch)
    {
        _management = management;
        _runtime = runtime;
        _elasticSearch = elasticSearch;
    }

    public async Task<FailedDeliveryPageDto> QueryAsync(FailedDeliveryQuery query)
    {
        ValidateQuery(query);
        var jobs = await LoadAllDeadLetterJobsAsync(query.ProcessInstanceId);
        var items = new List<FailedDeliveryDto>();
        foreach (var job in jobs.Where(IsFrameworkCallbackJob))
        {
            var item = await MapAsync(job, includeStackTrace: false);
            if (!string.IsNullOrWhiteSpace(query.BusinessId)
                && !string.Equals(item.BusinessId, query.BusinessId, StringComparison.Ordinal))
            {
                continue;
            }
            items.Add(item);
        }

        var ordered = items
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        var pageIndex = Math.Max(1, query.PageIndex);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        return new FailedDeliveryPageDto
        {
            Items = ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList(),
            Total = ordered.Count,
            PageIndex = pageIndex,
            PageSize = pageSize
        };
    }

    public async Task<FailedDeliveryDto?> GetAsync(string deliveryId)
    {
        var job = await _management.GetDeadLetterJobAsync(deliveryId);
        if (job == null || !IsFrameworkCallbackJob(job))
            return null;
        return await MapAsync(job, includeStackTrace: true);
    }

    public async Task RetryAsync(string deliveryId)
    {
        var job = await RequireActionableJobAsync(deliveryId);
        try
        {
            await _management.MoveDeadLetterJobToExecutableAsync(job.Id);
        }
        catch (FlowableApiException exception) when (exception.StatusCode == 404)
        {
            throw new ConcurrentUpdateException(
                "失败投递状态已变化，无法重复重试",
                "FAILED_DELIVERY_STATE_CHANGED");
        }
    }

    public async Task TerminateProcessAsync(string deliveryId, string reason)
    {
        var job = await RequireActionableJobAsync(deliveryId);
        await _runtime.DeleteProcessInstanceAsync(job.ProcessInstanceId, reason);
        await _elasticSearch.UpdateProcessStatusAsync(
            job.ProcessInstanceId,
            "terminated",
            DateTime.UtcNow);
    }

    private async Task<FlowableDeadLetterJob> RequireActionableJobAsync(
        string deliveryId)
    {
        var job = await _management.GetDeadLetterJobAsync(deliveryId);
        if (job == null || !IsFrameworkCallbackJob(job))
        {
            throw new ResourceNotFoundException(
                "失败投递不存在或状态已变化",
                "FAILED_DELIVERY_NOT_FOUND");
        }

        if (!await IsProcessActiveAsync(job.ProcessInstanceId))
        {
            throw new ConcurrentUpdateException(
                "流程实例已不处于运行状态",
                "PROCESS_NOT_ACTIVE");
        }
        return job;
    }

    private async Task<List<FlowableDeadLetterJob>> LoadAllDeadLetterJobsAsync(
        string? processInstanceId)
    {
        var result = new List<FlowableDeadLetterJob>();
        var start = 0;
        while (true)
        {
            var page = await _management.QueryDeadLetterJobsAsync(
                start,
                FlowablePageSize,
                processInstanceId);
            result.AddRange(page.Data);
            start += page.Data.Count;
            if (page.Data.Count == 0 || start >= page.Total)
                return result;
        }
    }

    private async Task<FailedDeliveryDto> MapAsync(
        FlowableDeadLetterJob job,
        bool includeStackTrace)
    {
        var metadata = await _elasticSearch.GetProcessMetadataAsync(
            job.ProcessInstanceId);
        var active = await IsProcessActiveAsync(job.ProcessInstanceId);
        var error = job.ExceptionMessage;
        if (includeStackTrace)
        {
            error = await _management.GetDeadLetterJobExceptionStackTraceAsync(job.Id)
                    ?? error;
        }

        return new FailedDeliveryDto
        {
            DeliveryId = job.Id,
            SourceId = job.Id,
            BusinessId = metadata?.BusinessId,
            ProcessInstanceId = job.ProcessInstanceId,
            ProcessState = active ? "running" : metadata?.Status ?? "not_active",
            ActivityId = job.ElementId,
            Target = metadata?.Callback?.Url,
            AttemptCount = null,
            LastHttpStatus = null,
            LastError = error,
            CreatedAt = job.CreateTime,
            LastFailedAt = null,
            AvailableActions = active
                ? new List<string>
                {
                    FailedDeliveryActions.RetryDelivery,
                    FailedDeliveryActions.TerminateProcess
                }
                : new List<string>()
        };
    }

    private async Task<bool> IsProcessActiveAsync(string processInstanceId)
    {
        try
        {
            await _runtime.GetProcessInstanceAsync(processInstanceId);
            return true;
        }
        catch (FlowableApiException exception) when (exception.StatusCode == 404)
        {
            return false;
        }
    }

    private static bool IsFrameworkCallbackJob(FlowableDeadLetterJob job)
        => !string.IsNullOrWhiteSpace(job.ElementId)
           && job.ElementId.EndsWith(
               "_framework_callback",
               StringComparison.Ordinal);

    private static void ValidateQuery(FailedDeliveryQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Source)
            && query.Source != FailedDeliverySources.FlowableAsyncJob)
            throw new BusinessException("source 仅支持 flowable_async_job", "INVALID_SOURCE");
        if (!string.IsNullOrWhiteSpace(query.Status)
            && query.Status != FailedDeliveryStatuses.DeadLetter)
            throw new BusinessException("status 仅支持 dead_letter", "INVALID_STATUS");
        if (!string.IsNullOrWhiteSpace(query.DeliveryType)
            && query.DeliveryType != FailedDeliveryTypes.ProcessCompleted)
            throw new BusinessException(
                "deliveryType 仅支持 process_completed",
                "INVALID_DELIVERY_TYPE");
    }
}
