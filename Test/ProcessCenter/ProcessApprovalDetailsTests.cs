using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class ProcessApprovalDetailsTests
{
    private static readonly DateTime Started = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Details_AreIsolatedByInstanceAndPreserveOpinionsAndTiming()
    {
        var es = new EsStub();
        es.Records.Add(new ProcessAuditRecord
        {
            Id = "audit-1", ProcessInstanceId = "instance-1", BusinessId = "business-1",
            TaskId = "task-1", OperatorId = "employee-1", Action = "approve",
            Comment = "同意", OperatedAt = Started.AddSeconds(12)
        });
        es.Records.Add(new ProcessAuditRecord { Id = "other", ProcessInstanceId = "instance-2" });
        var history = new HistoryStub();
        history.Tasks.Add(new FlowableHistoricTask
        {
            Id = "task-1", ProcessInstanceId = "instance-1", TaskDefinitionKey = "review",
            Name = "部门审签", Assignee = "employee-1", StartTime = Started,
            EndTime = Started.AddMilliseconds(12500), DurationInMillis = 12500, DeleteReason = "completed"
        });
        history.Tasks.Add(new FlowableHistoricTask { Id = "other", ProcessInstanceId = "instance-2" });

        var result = await new ProcessApprovalDetailsAppService(es, history).GetApprovalDetailsAsync("instance-1");

        Assert.Equal("instance-1", es.QueriedInstance);
        Assert.Equal("instance-1", history.QueriedInstance);
        Assert.Equal(2, result.ApprovalRound);
        Assert.Equal("request-2", result.RequestId);
        var task = Assert.Single(result.Tasks);
        Assert.Equal("部门审签", task.TaskName);
        Assert.Equal("completed", task.Status);
        Assert.Equal(12.5, task.DurationSeconds);
        var record = Assert.Single(result.AuditHistory);
        Assert.Equal("employee-1", record.OperatorId);
        Assert.Equal("同意", record.Comment);
        Assert.Equal(Started.AddSeconds(12), record.OperatedAt);
    }

    [Fact]
    public async Task AnApproveAuditDoesNotMakeAnActiveTaskCompleted()
    {
        var es = new EsStub();
        es.Records.Add(new ProcessAuditRecord
        {
            Id = "attempt", ProcessInstanceId = "instance-1", TaskId = "active", Action = "approve"
        });
        var history = new HistoryStub();
        history.Tasks.Add(new FlowableHistoricTask
        {
            Id = "active", ProcessInstanceId = "instance-1", StartTime = Started
        });
        var result = await new ProcessApprovalDetailsAppService(es, history).GetApprovalDetailsAsync("instance-1");

        var task = Assert.Single(result.Tasks);
        Assert.Equal("active", task.Status);
        Assert.Null(task.EndTime);
        Assert.Null(task.DurationSeconds);
        Assert.Equal("approve", Assert.Single(result.AuditHistory).Action);
    }

    [Fact]
    public async Task CancelledAndMissingHistoryDoNotBecomeCompletedTasks()
    {
        var es = new EsStub();
        es.Records.Add(new ProcessAuditRecord
        {
            Id = "missing", ProcessInstanceId = "instance-1", TaskId = "not-in-history", Action = "approve"
        });
        var history = new HistoryStub();
        history.Tasks.Add(new FlowableHistoricTask
        {
            Id = "cancelled", ProcessInstanceId = "instance-1", StartTime = Started,
            EndTime = Started.AddSeconds(3), DeleteReason = "Change activity to starter"
        });
        var result = await new ProcessApprovalDetailsAppService(es, history).GetApprovalDetailsAsync("instance-1");

        Assert.Equal("cancelled", Assert.Single(result.Tasks).Status);
        Assert.Equal("not-in-history", Assert.Single(result.AuditHistory).TaskId);
        Assert.DoesNotContain(result.Tasks, task => task.TaskId == "not-in-history");
    }

    [Fact]
    public async Task MissingInstanceReturnsAnExplicitNotFound()
    {
        var es = new EsStub { Metadata = null };
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            new ProcessApprovalDetailsAppService(es, new HistoryStub()).GetApprovalDetailsAsync("missing"));
        Assert.Equal("PROCESS_INSTANCE_NOT_FOUND", error.Code);
        Assert.Null(es.QueriedInstance);
    }

    [Fact]
    public async Task DependencyFailureDoesNotReturnPartialSuccess()
    {
        var history = new HistoryStub { Fail = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessApprovalDetailsAppService(new EsStub(), history).GetApprovalDetailsAsync("instance-1"));
    }

    private sealed class HistoryStub : IFlowableHistoryService
    {
        public List<FlowableHistoricTask> Tasks { get; } = new();
        public string? QueriedInstance { get; private set; }
        public bool Fail { get; set; }
        public Task<List<FlowableHistoricTask>> QueryHistoricTasksAsync(FlowableHistoricTaskQuery query)
        {
            QueriedInstance = query.ProcessInstanceId;
            return Fail ? Task.FromException<List<FlowableHistoricTask>>(new InvalidOperationException("unavailable"))
                : Task.FromResult(Tasks);
        }
    }

    private sealed class EsStub : IElasticSearchService
    {
        public ProcessMetadataDocument? Metadata { get; set; } = new()
        {
            ProcessInstanceId = "instance-1", BusinessId = "business-1", RequestId = "request-2",
            ApprovalRound = 2, CreatedTime = Started, Status = "completed", CompletedTime = Started.AddMinutes(1)
        };
        public List<ProcessAuditRecord> Records { get; } = new();
        public string? QueriedInstance { get; private set; }
        public Task<ProcessMetadataDocument> GetProcessMetadataAsync(string processInstanceId)
            => Task.FromResult(Metadata!);
        public Task<List<ProcessAuditRecord>> QueryAuditRecordsByProcessInstanceIdAsync(string processInstanceId)
        {
            QueriedInstance = processInstanceId;
            return Task.FromResult(Records);
        }
        public Task<List<ProcessAuditRecord>> QueryAuditRecordsByBusinessIdAsync(string businessId)
            => throw new NotSupportedException("Approval details must not query across approval rounds");
        public Task InitializeIndexesAsync() => throw new NotSupportedException();
        public Task IndexProcessMetadataAsync(ProcessMetadataDocument document) => throw new NotSupportedException();
        public Task<Dictionary<string, ProcessMetadataDocument>> GetProcessMetadataBatchAsync(List<string> ids) => throw new NotSupportedException();
        public Task<ProcessMetadataDocument> GetProcessMetadataByBusinessIdAsync(string id) => throw new NotSupportedException();
        public Task UpdateProcessStatusAsync(string id, string status, DateTime? completedTime = null) => throw new NotSupportedException();
        public Task DeleteProcessMetadataAsync(string id) => throw new NotSupportedException();
        public Task SaveNodeSemanticMapAsync(string key, Dictionary<string, NodeSemanticInfo> map) => throw new NotSupportedException();
        public Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(string key) => throw new NotSupportedException();
        public Task IndexAuditRecordAsync(ProcessAuditRecord record) => throw new NotSupportedException();
        public Task<(List<ProcessMetadataDocument> Items, int Total)> QueryProcessListAsync(ProcessListRequest request) => throw new NotSupportedException();
    }
}
