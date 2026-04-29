using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Infrastructure.Flowable
{
    public class FlowableTaskServiceImpl : IFlowableTaskService
    {
        private readonly FlowableHttpClient _http;
        private readonly ILogger<FlowableTaskServiceImpl> _logger;

        public FlowableTaskServiceImpl(
            FlowableHttpClient http,
            ILogger<FlowableTaskServiceImpl> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<FlowableTask> GetTaskAsync(string taskId)
        {
            var result = await _http.GetAsync<FlowableTaskResponse>(
                $"runtime/tasks/{taskId}");
            return MapTask(result);
        }

        public async Task<List<FlowableTask>> QueryTasksAsync(FlowableTaskQuery query)
        {
            var qs = BuildQueryString(query);
            var result = await _http.GetAsync<FlowableTaskListResponse>(
                $"runtime/tasks?{qs}&size=100");

            return result?.Data?.Select(MapTask).ToList()
                   ?? new List<FlowableTask>();
        }

        public async Task CompleteAsync(string taskId, Dictionary<string, object> variables)
        {
            var variableList = new List<object>();
            if (variables != null)
            {
                foreach (var kv in variables)
                {
                    var (type, value) = ResolveVariable(kv.Value);
                    variableList.Add(new
                    {
                        name = kv.Key,
                        value = kv.Value,
                        type = type
                    });
                }
            }

            await _http.PostAsync($"runtime/tasks/{taskId}", new
            {
                action = "complete",
                variables = variableList
            });

            _logger.LogInformation("任务已完成: {TaskId}", taskId);
        }

        public async Task ClaimAsync(string taskId, string userId)
        {
            await _http.PostAsync($"runtime/tasks/{taskId}", new
            {
                action = "claim",
                assignee = userId
            });
            _logger.LogInformation("任务已认领: {TaskId} → {UserId}", taskId, userId);
        }

        public async Task SetAssigneeAsync(string taskId, string userId)
        {
            await _http.PostAsync($"runtime/tasks/{taskId}", new
            {
                action = "assign",
                assignee = userId
            });
        }

        public async Task AddCandidateUsersAsync(string taskId, List<string> userIds)
        {
            foreach (var userId in userIds)
            {
                await _http.PostAsync($"runtime/tasks/{taskId}/identitylinks", new
                {
                    user = userId,
                    type = "candidate"
                });
            }
        }

        public async Task<List<string>> GetCandidateUsersAsync(string taskId)
        {
            var result = await _http.GetAsync<List<FlowableIdentityLink>>(
                $"runtime/tasks/{taskId}/identitylinks");

            return result?
                .Where(l => l.Type == "candidate" && !string.IsNullOrWhiteSpace(l.EffectiveUserId))
                .Select(l => l.EffectiveUserId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
        }

        public async Task ClearCandidateUsersAsync(string taskId)
        {
            var candidates = await GetCandidateUsersAsync(taskId);
            foreach (var userId in candidates)
            {
                await _http.DeleteAsync(
                    $"runtime/tasks/{taskId}/identitylinks/users/{userId}/candidate");
            }
        }

        // ── 私有辅助 ──────────────────────────────────────────────

        private static string BuildQueryString(FlowableTaskQuery query)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
                parts.Add($"processInstanceId={Uri.EscapeDataString(query.ProcessInstanceId)}");
            if (!string.IsNullOrWhiteSpace(query.Assignee))
                parts.Add($"assignee={Uri.EscapeDataString(query.Assignee)}");
            if (!string.IsNullOrWhiteSpace(query.CandidateUser))
                parts.Add($"candidateUser={Uri.EscapeDataString(query.CandidateUser)}");
            return string.Join("&", parts);
        }

        private static FlowableTask MapTask(FlowableTaskResponse r)
        {
            return new FlowableTask
            {
                Id = r.Id,
                Name = r.Name,
                ProcessInstanceId = r.ProcessInstanceId,
                ProcessDefinitionId = r.ProcessDefinitionId,
                TaskDefinitionKey = r.TaskDefinitionKey,
                Assignee = r.Assignee,
                Owner = r.Owner,
                CreateTime = r.CreateTime,
                Priority = r.Priority
            };
        }

        private static (string type, object value) ResolveVariable(object raw)
        {
            // System.Text.Json 反序列化 Dictionary<string,object> 时
            // 所有值都是 JsonElement，需要先拆包
            if (raw is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.True or
                    JsonValueKind.False => ("boolean", je.GetBoolean()),
                    JsonValueKind.Number when je.TryGetInt64(out var l) => ("integer", l),
                    JsonValueKind.Number when je.TryGetDouble(out var d) => ("double", d),
                    JsonValueKind.String => ("string", je.GetString()),
                    JsonValueKind.Array or
                    JsonValueKind.Object => ("json", je.GetRawText()),
                    _ => ("string", je.ToString())
                };
            }

            // 直接传入的强类型值
            return raw switch
            {
                bool b => ("boolean", (object)b),
                int i => ("integer", (object)(long)i),
                long l => ("integer", (object)l),
                double d => ("double", (object)d),
                float f => ("double", (object)(double)f),
                decimal dec => ("double", (object)(double)dec),
                System.Collections.IList => ("json", System.Text.Json.JsonSerializer.Serialize(raw)),
                string s => ("string", (object)s),
                _ => ("string", (object)(raw?.ToString() ?? ""))
            };
        }

        // ── Flowable REST 响应 DTO ──────────────────────────────
        private class FlowableTaskListResponse
        {
            [JsonPropertyName("data")]
            public List<FlowableTaskResponse> Data { get; set; }
        }

        private class FlowableTaskResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("processInstanceId")] public string ProcessInstanceId { get; set; }
            [JsonPropertyName("processDefinitionId")] public string ProcessDefinitionId { get; set; }
            [JsonPropertyName("taskDefinitionKey")] public string TaskDefinitionKey { get; set; }
            [JsonPropertyName("assignee")] public string Assignee { get; set; }
            [JsonPropertyName("owner")] public string Owner { get; set; }
            [JsonPropertyName("createTime")] public DateTime CreateTime { get; set; }
            [JsonPropertyName("priority")] public int Priority { get; set; }
        }

        private class FlowableIdentityLink
        {
            [JsonPropertyName("user")] public string FlowableUser { get; set; }
            [JsonPropertyName("userId")] public string UserId { get; set; }
            [JsonPropertyName("type")] public string Type { get; set; }

            [JsonIgnore]
            public string EffectiveUserId =>
                !string.IsNullOrWhiteSpace(UserId) ? UserId : FlowableUser;
        }
    }
}
