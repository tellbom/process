using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class CompletedRecordRenderBuilderTests
{
    [Fact]
    public void Build_UsesTaskIdAndCountsRoundsPerNodeAfterRejectLoop()
    {
        var auditRecords = new List<ProcessAuditRecord>
        {
            Audit("task-a-1", "node-a", "EMP_A", "approve", 1),
            Audit("task-b-1", "node-b", "EMP_B", "reject", 2),
            Audit("task-a-2", "node-a", "EMP_A", "approve", 3)
        };
        var historicTasks = new List<FlowableHistoricTask>
        {
            Historic("task-a-1", "node-a", "EMP_A", 10, 20),
            Historic("task-b-1", "node-b", "EMP_B", 30, 50),
            Historic("task-a-2", "node-a", "EMP_A", 60, 90)
        };

        var result = CompletedRecordRenderBuilder.Build(auditRecords, historicTasks);

        Assert.Collection(result,
            first =>
            {
                Assert.Equal("task-a-1", first.TaskId);
                Assert.Equal(1, first.Round);
                Assert.Equal(10, first.DurationSeconds);
            },
            second =>
            {
                Assert.Equal("task-b-1", second.TaskId);
                Assert.Equal(1, second.Round);
                Assert.Equal("EMP_B", second.OperatorId);
            },
            third =>
            {
                Assert.Equal("task-a-2", third.TaskId);
                Assert.Equal(2, third.Round);
                Assert.Equal(30, third.DurationSeconds);
                Assert.Equal(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc).AddSeconds(60), third.StartTime);
            });
    }

    [Fact]
    public void Build_KeepsReassignAndCompletionForSameTaskInOneRound()
    {
        var auditRecords = new List<ProcessAuditRecord>
        {
            Audit("task-a-1", "node-a", "EMP_ADMIN", "reassign", 1),
            Audit("task-a-1", "node-a", "EMP_NEW", "approve", 2),
            Audit("task-a-2", "node-a", "EMP_NEXT", "approve", 3)
        };
        var historicTasks = new List<FlowableHistoricTask>
        {
            Historic("task-a-1", "node-a", "EMP_NEW", 10, 20),
            Historic("task-a-2", "node-a", "EMP_NEXT", 30, 40)
        };

        var result = CompletedRecordRenderBuilder.Build(auditRecords, historicTasks);

        Assert.Equal(new[] { 1, 1, 2 }, result.Select(record => record.Round));
    }

    private static ProcessAuditRecord Audit(
        string taskId,
        string nodeId,
        string operatorId,
        string action,
        int operatedMinute) => new()
    {
        TaskId = taskId,
        TaskDefinitionKey = nodeId,
        OperatorId = operatorId,
        Action = action,
        OperatedAt = new DateTime(2026, 8, 31, 0, operatedMinute, 0, DateTimeKind.Utc)
    };

    private static FlowableHistoricTask Historic(
        string taskId,
        string nodeId,
        string assignee,
        int startSecond,
        int endSecond) => new()
    {
        Id = taskId,
        TaskDefinitionKey = nodeId,
        Assignee = assignee,
        StartTime = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc).AddSeconds(startSecond),
        EndTime = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc).AddSeconds(endSecond),
        DurationInMillis = (endSecond - startSecond) * 1000L
    };
}
