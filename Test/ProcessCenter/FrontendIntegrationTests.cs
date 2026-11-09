using System.Reflection;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class FrontendIntegrationTests
{
    [Fact]
    public void CursorPreservesStableSortIncludingSameTimestampInstances()
    {
        var item = Metadata("round-1");
        item.CreatedTime = new DateTime(2026, 10, 19, 0, 1, 2, 123, DateTimeKind.Utc);
        var values = ProcessListCursor.Decode(ProcessListCursor.Encode(item));
        Assert.Equal(new DateTimeOffset(item.CreatedTime).ToUnixTimeMilliseconds(), values![0]);
        Assert.Equal("round-1", values[1]);
        Assert.Null(ProcessListCursor.Decode(null));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("e30=")]
    [InlineData("W10=")]
    [InlineData("WzEsIiJd")]
    [InlineData("WzEsM10=")]
    public void MalformedCursorIsBusinessError(string cursor)
        => Assert.Equal("INVALID_CURSOR", Assert.Throws<BusinessException>(() => ProcessListCursor.Decode(cursor)).Code);

    [Fact]
    public async Task HistoricRoundProgressNeverResolvesTheLatestRound()
    {
        var es = Proxy<IElasticSearchService>((method, args) => method switch
        {
            "GetProcessMetadataAsync" => Task.FromResult(Metadata((string)args![0]!)),
            "QueryAuditRecordsByBusinessIdAsync" => Audit(args),
            _ => throw new Exception("Unexpected metadata query: " + method)
        });
        var tasks = Proxy<IFlowableTaskService>((method, args) =>
        {
            Assert.Equal("QueryTasksAsync", method);
            Assert.Equal("round-1", ((FlowableTaskQuery)args![0]!).ProcessInstanceId);
            return Task.FromResult(new List<FlowableTask>());
        });
        var service = QueryService(es, tasks);
        var progress = await service.GetProcessProgressAsync("same-business", "round-1");
        Assert.Equal("round-1", progress.ProcessInstanceId);
        Assert.Equal("old-task", Assert.Single(progress.AuditHistory).TaskId);
    }

    [Fact]
    public async Task WrongBusinessCannotReadAnotherInstancesProgress()
    {
        var es = Proxy<IElasticSearchService>((method, args) => Task.FromResult(Metadata("round-1")));
        var error = await Assert.ThrowsAsync<BusinessException>(() => QueryService(es, null!)
            .GetProcessProgressAsync("wrong-business", "round-1"));
        Assert.Equal("PROCESS_INSTANCE_NOT_FOUND", error.Code);
    }

    [Theory]
    [InlineData("196045", 1)]
    [InlineData("other", 0)]
    [InlineData(null, 0)]
    public async Task PreciseTaskLinkFindsTaskOutsideFirstPageAndChecksItsUser(string? assignee, int expected)
    {
        var task = new FlowableTask { Id = "older-task", Assignee = assignee!, ProcessInstanceId = "round-1", TaskDefinitionKey = "review", Name = "Review" };
        var tasks = Proxy<IFlowableTaskService>((method, args) => method switch
        {
            "GetTaskAsync" => Task.FromResult(task),
            "GetCandidateUsersAsync" => Task.FromResult(new List<string>()),
            _ => throw new Exception("Precise lookup must not scan a first page: " + method)
        });
        var es = Proxy<IElasticSearchService>((method, args) => Task.FromResult(new Dictionary<string, ProcessMetadataDocument> { ["round-1"] = Metadata("round-1") }));
        var slots = Proxy<IProcessSlotConfigProvider>((method, args) => Task.FromResult(new Dictionary<string, NodeSemanticInfo>()));
        var service = new TaskExecutionAppService(null!, tasks, es, slots, null!, null!,
            NullLogger<TaskExecutionAppService>.Instance, null!, null!, null!, Options.Create(new Dm8Options()));
        var result = await service.GetPendingTasksAsync(new GetPendingTasksRequest { EmployeeId = "196045", TaskId = "older-task", PageSize = 2 });
        Assert.Equal(expected, result.Total);
        Assert.Equal(expected, result.Items.Count);
    }

    private static Task<List<ProcessAuditRecord>> Audit(object?[]? args)
    {
        Assert.Equal("round-1", args![1]);
        return Task.FromResult(new List<ProcessAuditRecord>
        {
            new() { ProcessInstanceId = "round-1", TaskId = "old-task" },
            new() { ProcessInstanceId = "round-2", TaskId = "new-task" }
        });
    }

    [Fact]
    public async Task HistoricListRowCannotTerminateTheLatestApprovalRound()
    {
        var es = Proxy<IElasticSearchService>((method, args) => Task.FromResult(Metadata("round-2")));
        var service = new ProcessLifecycleAppService(null!, null!, null!, es, null!, null!, null!, null!,
            Options.Create(new BusinessTypeProcessMapping()), Options.Create(new FlowableWrapper.Infrastructure.Flowable.FlowableOptions()),
            NullLogger<ProcessLifecycleAppService>.Instance, null!, null!, Options.Create(new Dm8Options()));
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.TerminateProcessAsync(
            new TerminateProcessRequest { BusinessId = "same-business", ProcessInstanceId = "round-1", Reason = "withdraw" }));
        Assert.Equal("PROCESS_INSTANCE_CHANGED", error.Code);
    }

    private static ProcessMetadataDocument Metadata(string instanceId) => new()
    {
        BusinessId = "same-business", ProcessInstanceId = instanceId, ProcessDefinitionKey = "review", Status = "completed"
    };

    private static ProcessQueryAppService QueryService(IElasticSearchService es, IFlowableTaskService tasks)
        => new(tasks, es, null!, null!, Options.Create(new Dm8Options()), NullLogger<ProcessQueryAppService>.Instance);

    private static T Proxy<T>(Func<string, object?[]?, object?> action) where T : class
    {
        var proxy = DispatchProxy.Create<T, ReadProxy>();
        ((ReadProxy)(object)proxy).Action = action;
        return proxy;
    }
    public class ReadProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Action { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!.Name, args);
    }
}
