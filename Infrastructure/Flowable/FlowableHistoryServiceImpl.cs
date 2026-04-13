using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Infrastructure.Flowable
{
    public class FlowableHistoryServiceImpl : IFlowableHistoryService
    {
        private readonly FlowableHttpClient _http;
        private readonly ILogger<FlowableHistoryServiceImpl> _logger;

        public FlowableHistoryServiceImpl(
            FlowableHttpClient http,
            ILogger<FlowableHistoryServiceImpl> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<List<FlowableHistoricTask>> QueryHistoricTasksAsync(
            FlowableHistoricTaskQuery query)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
                parts.Add($"processInstanceId={Uri.EscapeDataString(query.ProcessInstanceId)}");
            if (query.Finished.HasValue)
                parts.Add($"finished={query.Finished.Value.ToString().ToLower()}");
            if (!string.IsNullOrWhiteSpace(query.TaskDefinitionKey))
                parts.Add($"taskDefinitionKey={Uri.EscapeDataString(query.TaskDefinitionKey)}");
            parts.Add("size=200");

            var qs = string.Join("&", parts);
            var result = await _http.GetAsync<FlowableHistoricTaskListResponse>(
                $"history/historic-task-instances?{qs}");

            return result?.Data?.Select(r => new FlowableHistoricTask
            {
                Id = r.Id,
                Name = r.Name,
                ProcessInstanceId = r.ProcessInstanceId,
                TaskDefinitionKey = r.TaskDefinitionKey,
                Assignee = r.Assignee,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                DurationInMillis = r.DurationInMillis,
                DeleteReason = r.DeleteReason
            }).ToList() ?? new List<FlowableHistoricTask>();
        }

        private class FlowableHistoricTaskListResponse
        {
            [JsonPropertyName("data")]
            public List<FlowableHistoricTaskResponse> Data { get; set; }
        }

        private class FlowableHistoricTaskResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("processInstanceId")] public string ProcessInstanceId { get; set; }
            [JsonPropertyName("taskDefinitionKey")] public string TaskDefinitionKey { get; set; }
            [JsonPropertyName("assignee")] public string Assignee { get; set; }
            [JsonPropertyName("startTime")] public DateTime StartTime { get; set; }
            [JsonPropertyName("endTime")] public DateTime? EndTime { get; set; }
            [JsonPropertyName("durationInMillis")] public long? DurationInMillis { get; set; }
            [JsonPropertyName("deleteReason")] public string DeleteReason { get; set; }
        }
    }
}
