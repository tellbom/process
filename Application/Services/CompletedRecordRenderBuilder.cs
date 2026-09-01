using System;
using System.Collections.Generic;
using System.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;

namespace FlowableWrapper.Application.Services
{
    internal static class CompletedRecordRenderBuilder
    {
        internal static List<CompletedRecordRenderDto> Build(
            List<ProcessAuditRecord> auditRecords,
            List<FlowableHistoricTask> historicTasks)
        {
            var historicByTaskId = historicTasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Id))
                .GroupBy(task => task.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var roundByNode = new Dictionary<string, int>();
            var roundByTask = new Dictionary<(string NodeId, string TaskId), int>();

            return auditRecords
                .OrderBy(record => record.OperatedAt)
                .Select(record =>
                {
                    var taskKey = (record.TaskDefinitionKey, record.TaskId);
                    if (!roundByTask.TryGetValue(taskKey, out var round))
                    {
                        roundByNode.TryGetValue(record.TaskDefinitionKey, out var previousRound);
                        round = previousRound + 1;
                        roundByNode[record.TaskDefinitionKey] = round;
                        roundByTask[taskKey] = round;
                    }

                    var startTime = record.OperatedAt;
                    var endTime = record.OperatedAt;
                    long duration = 0;

                    if (!string.IsNullOrWhiteSpace(record.TaskId)
                        && historicByTaskId.TryGetValue(record.TaskId, out var historic)
                        && historic.EndTime.HasValue)
                    {
                        startTime = historic.StartTime;
                        endTime = historic.EndTime.Value;
                        duration = historic.DurationInMillis.HasValue
                            ? historic.DurationInMillis.Value / 1000
                            : (long)(endTime - startTime).TotalSeconds;
                    }

                    var outcome = record.Action == "reject"
                        ? "rejected_return"
                        : record.Action == "reassign"
                            ? "reassigned"
                            : "approved";

                    return new CompletedRecordRenderDto
                    {
                        TaskId = record.TaskId,
                        NodeId = record.TaskDefinitionKey,
                        NodeName = record.NodeSemantic ?? record.TaskDefinitionKey,
                        OperatorId = record.OperatorId,
                        StartTime = startTime,
                        EndTime = endTime,
                        DurationSeconds = duration,
                        Outcome = outcome,
                        RejectReason = record.RejectReason,
                        Comment = record.Comment,
                        Round = round
                    };
                })
                .ToList();
        }
    }
}
