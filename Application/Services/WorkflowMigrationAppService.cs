using System.Text.Json;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;

namespace FlowableWrapper.Application.Services;

/// <summary>
/// Resumable compatibility migration from the legacy ES metadata projection to
/// the DM8 business binding table. This migrates only reliability fields and
/// never copies Flowable runtime tables.
/// </summary>
public sealed class WorkflowMigrationAppService
{
    private readonly IElasticSearchService _elasticSearch;
    private readonly IWorkflowReliabilityStore _store;
    private readonly ILogger<WorkflowMigrationAppService> _logger;

    public WorkflowMigrationAppService(
        IElasticSearchService elasticSearch,
        IWorkflowReliabilityStore store,
        ILogger<WorkflowMigrationAppService> logger)
    {
        _elasticSearch = elasticSearch;
        _store = store;
        _logger = logger;
    }

    public async Task<WorkflowBindingMigrationResult> MigrateEsBindingsPageAsync(
        int pageIndex,
        int pageSize,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await _elasticSearch.QueryProcessListAsync(
            new ProcessListRequest
            {
                PageIndex = pageIndex,
                PageSize = pageSize
            });
        var result = new WorkflowBindingMigrationResult
        {
            PageIndex = pageIndex,
            PageSize = pageSize,
            SourceTotal = total,
            SourceCount = items.Count,
            DryRun = dryRun
        };
        var existingByProcessInstance =
            await _store.GetBusinessesByProcessInstancesAsync(
                items.Select(item => item.ProcessInstanceId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()!,
                cancellationToken);

        foreach (var metadata in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(metadata.BusinessId)
                    || string.IsNullOrWhiteSpace(metadata.ProcessInstanceId)
                    || string.IsNullOrWhiteSpace(metadata.ProcessDefinitionKey))
                {
                    result.Invalid++;
                    result.Issues.Add(new WorkflowMigrationIssue(
                        metadata.BusinessId,
                        metadata.ProcessInstanceId,
                        "Legacy metadata is missing a required binding field."));
                    continue;
                }

                if (existingByProcessInstance.TryGetValue(
                        metadata.ProcessInstanceId,
                        out var existing))
                {
                    if (!string.Equals(
                            existing.BusinessId,
                            metadata.BusinessId,
                            StringComparison.Ordinal))
                    {
                        result.Conflicts++;
                        result.Issues.Add(new WorkflowMigrationIssue(
                            metadata.BusinessId,
                            metadata.ProcessInstanceId,
                            $"Process instance is already bound to business '{existing.BusinessId}'."));
                    }
                    else
                    {
                        result.Skipped++;
                    }
                    continue;
                }

                if (dryRun)
                {
                    result.Planned++;
                    continue;
                }

                var reservation = await _store.ReserveBusinessAsync(
                    new ReserveBusinessCommand
                    {
                        BusinessId = metadata.BusinessId,
                        BusinessType = metadata.BusinessType
                                       ?? metadata.ProcessDefinitionKey,
                        ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                        CallbackConfigSnapshot = metadata.Callback == null
                            ? null
                            : JsonSerializer.Serialize(metadata.Callback)
                    },
                    cancellationToken);
                if (!reservation.Created)
                {
                    result.Skipped++;
                    continue;
                }

                // Legacy ES documents normally have no trustworthy version.
                // Null deliberately selects the explicit legacy ES rule path.
                await _store.BindStartedProcessAsync(
                    metadata.BusinessId,
                    metadata.ProcessInstanceId,
                    metadata.ProcessDefinitionVersion,
                    cancellationToken);
                if (metadata.RecommendedAssigneesSnapshot?.Count > 0)
                {
                    await _store.UpdateRecommendedAssigneesSnapshotAsync(
                        metadata.BusinessId,
                        JsonSerializer.Serialize(
                            metadata.RecommendedAssigneesSnapshot),
                        cancellationToken);
                }

                var flowState = NormalizeFlowState(metadata.Status);
                if (!string.Equals(flowState, "running", StringComparison.Ordinal))
                {
                    await _store.MarkBusinessFlowStateAsync(
                        metadata.BusinessId,
                        flowState,
                        cancellationToken);
                }
                result.Migrated++;
            }
            catch (Exception exception)
            {
                result.Failed++;
                result.Issues.Add(new WorkflowMigrationIssue(
                    metadata.BusinessId,
                    metadata.ProcessInstanceId,
                    exception.Message));
                _logger.LogError(
                    exception,
                    "ES binding migration failed. BusinessId={BusinessId}, ProcessInstanceId={ProcessInstanceId}",
                    metadata.BusinessId,
                    metadata.ProcessInstanceId);
            }
        }
        return result;
    }

    private static string NormalizeFlowState(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "terminated" => "terminated",
            "reconcile_required" => "reconcile_required",
            _ => "running"
        };
}

public sealed class WorkflowBindingMigrationResult
{
    public int PageIndex { get; init; }
    public int PageSize { get; init; }
    public int SourceTotal { get; init; }
    public int SourceCount { get; init; }
    public bool DryRun { get; init; }
    public int Planned { get; set; }
    public int Migrated { get; set; }
    public int Skipped { get; set; }
    public int Invalid { get; set; }
    public int Conflicts { get; set; }
    public int Failed { get; set; }
    public List<WorkflowMigrationIssue> Issues { get; } = new();
}

public sealed record WorkflowMigrationIssue(
    string? BusinessId,
    string? ProcessInstanceId,
    string Message);
