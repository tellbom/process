using System.Reflection;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class ProcessStatusConsistencyTests
{
    private static readonly DateTime FinishedAt = new(2026, 11, 11, 8, 30, 0);
    private static WorkflowBusinessInstance Binding(string id = "round-1") => new()
    {
        BusinessId = "business", ProcessInstanceId = id, RequestId = "request", ApprovalRound = 1,
        BusinessTitle = "已完成申请", BusinessType = "review", CreatedBy = "001",
        ProcessDefinitionKey = "review", ProcessDefinitionVersion = 2,
        FlowState = "completed", CallbackState = "dead_letter", CompletedAt = FinishedAt,
        CreatedAt = FinishedAt.AddHours(-1).AddTicks(123456)
    };
    private static ProcessMetadataDocument StaleProjection(string id = "round-1") => new()
    {
        BusinessId = "business", ProcessInstanceId = id, ProcessDefinitionKey = "review",
        Status = "running", CompletedTime = null
    };

    [Fact]
    public async Task ListUsesDurableStatusAndFiltersBeforePagingEvenWhenEsIsStaleOrUnavailable()
    {
        var es = Proxy<IElasticSearchService>((_, _) => throw new Exception("List must not depend on ES"));
        var store = Proxy<IWorkflowReliabilityStore>((method, args) =>
        {
            Assert.Equal("QueryBusinessesAsync", method);
            var query = (WorkflowBusinessQuery)args![0]!;
            Assert.Equal("completed", query.FlowState);
            Assert.Equal("001", query.CreatedBy);
            Assert.Equal("review", query.BusinessType);
            Assert.Equal("business", query.BusinessId);
            Assert.Equal(FinishedAt.AddDays(-1), query.CreatedTimeFrom);
            Assert.Equal(FinishedAt, query.CreatedTimeTo);
            Assert.Equal(10, query.Start);
            Assert.Equal(10, query.Size);
            return Task.FromResult(((IReadOnlyList<WorkflowBusinessInstance>)new[] { Binding() }, 11));
        });
        var result = await Service(es, store).GetProcessListAsync(new()
        {
            BusinessId = "business", BusinessType = "review", Status = "completed", CreatedBy = "001",
            CreatedTimeFrom = FinishedAt.AddDays(-1), CreatedTimeTo = FinishedAt, PageIndex = 2, PageSize = 10
        });
        var row = Assert.Single(result.Items);
        Assert.Equal("completed", row.Status);
        Assert.Equal(FinishedAt, row.CompletedTime);
        Assert.Equal("已完成申请", row.BusinessTitle);
        Assert.Equal(11, result.Total);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task ProgressAndStatusAgreeDespiteAStaleRunningProjectionAndFailedDelivery()
    {
        var es = Projection();
        var store = Proxy<IWorkflowReliabilityStore>((method, args) =>
        {
            Assert.Contains(method, new[] { "GetBusinessByBusinessIdAsync", "GetBusinessByProcessInstanceAsync" });
            return Task.FromResult<WorkflowBusinessInstance?>(Binding());
        });
        var tasks = Proxy<IFlowableTaskService>((_, _) => Task.FromResult(new List<FlowableTask>()));
        var service = Service(es, store, tasks);
        var status = await service.GetProcessStatusByBusinessIdAsync("business");
        var latest = await service.GetProcessProgressAsync("business");
        var exact = await service.GetProcessProgressAsync("business", "round-1");
        foreach (var progress in new[] { latest, exact })
        {
            Assert.Equal(status.Status, progress.Status);
            Assert.Equal("completed", progress.Status);
            Assert.Equal(FinishedAt, progress.CompletedTime);
            Assert.Empty(progress.CurrentNodes);
        }
    }

    [Fact]
    public async Task FlowRenderUsesTheSameBindingForItsStatusAndRound()
    {
        var store = Proxy<IWorkflowReliabilityStore>((_, _) => Task.FromResult<WorkflowBusinessInstance?>(Binding()));
        var tasks = Proxy<IFlowableTaskService>((_, _) => Task.FromResult(new List<FlowableTask>()));
        var history = Proxy<IFlowableHistoryService>((_, _) => Task.FromResult(new List<FlowableHistoricTask>()));
        var repository = Proxy<IFlowableRepositoryService>((_, _) => Task.FromResult<FlowableProcessDefinition?>(null));
        var service = new ProcessFlowRenderAppService(Projection(), tasks, history, repository, null!,
            NullLogger<ProcessFlowRenderAppService>.Instance, store, Options.Create(new Dm8Options { Enabled = true }));
        var result = await service.GetFlowRenderAsync("business");
        Assert.Equal("round-1", result.ProcessInstanceId);
        Assert.Equal("completed", result.Status);
        Assert.Equal(FinishedAt, result.CompletedTime);
    }

    [Fact]
    public async Task ExplicitHistoricRoundDoesNotResolveTheLatestBusinessBinding()
    {
        var store = Proxy<IWorkflowReliabilityStore>((method, args) =>
        {
            Assert.Equal("GetBusinessByProcessInstanceAsync", method);
            Assert.Equal("round-1", args![0]);
            return Task.FromResult<WorkflowBusinessInstance?>(Binding());
        });
        var tasks = Proxy<IFlowableTaskService>((_, _) => Task.FromResult(new List<FlowableTask>()));
        var result = await Service(Projection(), store, tasks).GetProcessProgressAsync("business", "round-1");
        Assert.Equal("round-1", result.ProcessInstanceId);
        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public void Dm8CursorPreservesSubMillisecondPrecisionAndRejectsForeignOrMalformedCursors()
    {
        var item = Binding();
        var cursor = WorkflowBusinessListCursor.Decode(WorkflowBusinessListCursor.Encode(item));
        Assert.Equal(item.CreatedAt.Ticks, cursor.CreatedAt!.Value.Ticks);
        Assert.Equal(item.ProcessInstanceId, cursor.InstanceId);
        foreach (var invalid in new[] { "bad", "e30=", "W10=", ProcessListCursor.Encode(StaleProjection()) })
            Assert.Equal("INVALID_CURSOR", Assert.Throws<BusinessException>(() => WorkflowBusinessListCursor.Decode(invalid)).Code);
    }

    [Fact]
    public async Task DisabledDm8KeepsLegacyListBehavior()
    {
        var es = Proxy<IElasticSearchService>((method, _) =>
        {
            Assert.Equal("QueryProcessListAsync", method);
            return Task.FromResult((new List<ProcessMetadataDocument> { StaleProjection() }, 1));
        });
        var result = await new ProcessQueryAppService(null!, es, null!, null!, Options.Create(new Dm8Options()),
            NullLogger<ProcessQueryAppService>.Instance).GetProcessListAsync(new());
        Assert.Equal("running", Assert.Single(result.Items).Status);
    }

    private static IElasticSearchService Projection() => Proxy<IElasticSearchService>((method, args) => method switch
    {
        "GetProcessMetadataAsync" => Task.FromResult(StaleProjection((string)args![0]!)),
        "QueryAuditRecordsByBusinessIdAsync" => Task.FromResult(new List<ProcessAuditRecord>()),
        _ => throw new Exception("Must use an exact DM8 instance: " + method)
    });
    private static ProcessQueryAppService Service(IElasticSearchService es, IWorkflowReliabilityStore store, IFlowableTaskService? tasks = null)
        => new(tasks!, es, null!, store, Options.Create(new Dm8Options { Enabled = true }), NullLogger<ProcessQueryAppService>.Instance);
    private static T Proxy<T>(Func<string, object?[]?, object?> action) where T : class
    {
        var proxy = DispatchProxy.Create<T, FrontendIntegrationTests.ReadProxy>();
        ((FrontendIntegrationTests.ReadProxy)(object)proxy).Action = action;
        return proxy;
    }
}
