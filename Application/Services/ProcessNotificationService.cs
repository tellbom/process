using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Application.Services
{
    public class ProcessNotificationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IFlowableTaskService _taskService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ProcessNotificationOptions _options;
        private readonly ILogger<ProcessNotificationService> _logger;

        public ProcessNotificationService(
            IFlowableTaskService taskService,
            IHttpClientFactory httpClientFactory,
            IOptions<ProcessNotificationOptions> options,
            ILogger<ProcessNotificationService> logger)
        {
            _taskService = taskService;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task SendNextStepNotificationSafeAsync(
            ProcessMetadataDocument metadata,
            IReadOnlyCollection<string> previousActiveTaskIds)
        {
            await SendCurrentTaskNotificationsSafeAsync(
                metadata,
                previousActiveTaskIds,
                "流程待办提醒",
                task => BuildNextStepContent(metadata, task),
                "next_step");
        }

        public async Task SendRejectNotificationSafeAsync(
            ProcessMetadataDocument metadata,
            IReadOnlyCollection<string> previousActiveTaskIds,
            string? rejectReason)
        {
            await SendCurrentTaskNotificationsSafeAsync(
                metadata,
                previousActiveTaskIds,
                "流程驳回提醒",
                task => BuildRejectContent(metadata, task, rejectReason),
                "reject");
        }

        private async Task SendCurrentTaskNotificationsSafeAsync(
            ProcessMetadataDocument metadata,
            IReadOnlyCollection<string> previousActiveTaskIds,
            string title,
            Func<FlowableTask, string> contentFactory,
            string scene)
        {
            if (!_options.Enabled)
                return;

            if (metadata == null || string.IsNullOrWhiteSpace(metadata.ProcessInstanceId))
                return;

            try
            {
                var tasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
                {
                    ProcessInstanceId = metadata.ProcessInstanceId
                });

                var previousIds = new HashSet<string>(
                    previousActiveTaskIds ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var newTasks = tasks
                    .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                    .Where(t => !previousIds.Contains(t.Id))
                    .ToList();

                if (!newTasks.Any())
                {
                    _logger.LogDebug(
                        "Process notification skipped because Flowable has no new active tasks. BusinessId={BusinessId}, Scene={Scene}",
                        metadata.BusinessId, scene);
                    return;
                }

                var token = await GetAccessTokenAsync();
                foreach (var task in newTasks)
                {
                    var receiverIds = await ResolveReceiverIdsAsync(task);
                    if (!receiverIds.Any())
                    {
                        _logger.LogWarning(
                            "Process notification skipped because task has no user receivers. BusinessId={BusinessId}, TaskId={TaskId}, Scene={Scene}",
                            metadata.BusinessId, task.Id, scene);
                        continue;
                    }

                    await SendMessageAsync(
                        token,
                        task.Id,
                        title,
                        contentFactory(task),
                        BuildTaskUrl(task),
                        receiverIds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Process notification failed. BusinessId={BusinessId}, ProcessInstanceId={ProcessInstanceId}, Scene={Scene}",
                    metadata?.BusinessId,
                    metadata?.ProcessInstanceId,
                    scene);
            }
        }

        private async Task<List<string>> ResolveReceiverIdsAsync(FlowableTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.Assignee))
                return new List<string> { task.Assignee };

            var candidates = await _taskService.GetCandidateUsersAsync(task.Id);
            return candidates
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<string> GetAccessTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(_options.TokenEndpoint))
                throw new InvalidOperationException("ProcessNotification:TokenEndpoint is required.");
            if (string.IsNullOrWhiteSpace(_options.ClientId))
                throw new InvalidOperationException("ProcessNotification:ClientId is required.");
            if (string.IsNullOrWhiteSpace(_options.ClientSecret))
                throw new InvalidOperationException("ProcessNotification:ClientSecret is required.");

            var client = _httpClientFactory.CreateClient("ProcessNotificationAuth");
            using var response = await client.PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret
                }));

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"User center token request failed. Status={(int)response.StatusCode}, Body={body}");

            var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(token?.AccessToken))
                throw new InvalidOperationException("User center token response does not contain access_token.");

            return token.AccessToken;
        }

        private async Task SendMessageAsync(
            string token,
            string businessId,
            string title,
            string content,
            string url,
            List<string> receiverIds)
        {
            if (string.IsNullOrWhiteSpace(_options.MessageCenterBaseUrl))
                throw new InvalidOperationException("ProcessNotification:MessageCenterBaseUrl is required.");

            var client = _httpClientFactory.CreateClient("ProcessMessageCenter");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(_options.MessageCenterBaseUrl.TrimEnd('/') + "/"), _options.SendPath.TrimStart('/')));

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new SendMessageRequest
            {
                BusinessType = _options.BusinessType,
                BusinessId = businessId,
                Title = title,
                Content = content,
                Url = url,
                Receivers = receiverIds
                    .Select(id => new MessageReceiver { Type = "user", Id = id })
                    .ToList()
            }, options: JsonOptions);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Message center send failed. Status={(int)response.StatusCode}, Body={body}");

            _logger.LogInformation(
                "Process notification sent. BusinessId={BusinessId}, Receivers=[{Receivers}], MessageBusinessId={MessageBusinessId}",
                businessId,
                string.Join(",", receiverIds),
                businessId);
        }

        private string BuildNextStepContent(ProcessMetadataDocument metadata, FlowableTask task)
        {
            var nodeName = ResolveNodeName(metadata, task);
            return $"流程已进入{nodeName}，请及时处理。";
        }

        private string BuildRejectContent(
            ProcessMetadataDocument metadata,
            FlowableTask task,
            string? rejectReason)
        {
            var nodeName = ResolveNodeName(metadata, task);
            if (string.IsNullOrWhiteSpace(rejectReason))
                return $"流程已驳回至{nodeName}，请及时处理。";

            return $"流程已驳回至{nodeName}，请及时处理。驳回原因：{rejectReason}";
        }

        private string ResolveNodeName(ProcessMetadataDocument metadata, FlowableTask task)
        {
            if (metadata?.NodeSemanticMap != null
                && metadata.NodeSemanticMap.TryGetValue(task.TaskDefinitionKey, out var node)
                && !string.IsNullOrWhiteSpace(node.NodeSemantic))
                return node.NodeSemantic;

            return string.IsNullOrWhiteSpace(task.Name)
                ? task.TaskDefinitionKey
                : task.Name;
        }

        private string BuildTaskUrl(FlowableTask task)
        {
            var template = string.IsNullOrWhiteSpace(_options.TaskUrlTemplate)
                ? "/process/tasks/{taskId}"
                : _options.TaskUrlTemplate;

            return template
                .Replace("{taskId}", Uri.EscapeDataString(task.Id ?? string.Empty))
                .Replace("{processInstanceId}", Uri.EscapeDataString(task.ProcessInstanceId ?? string.Empty))
                .Replace("{taskDefinitionKey}", Uri.EscapeDataString(task.TaskDefinitionKey ?? string.Empty));
        }

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;
        }

        private sealed class SendMessageRequest
        {
            public string BusinessType { get; set; } = string.Empty;

            public string BusinessId { get; set; } = string.Empty;

            public string Title { get; set; } = string.Empty;

            public string Content { get; set; } = string.Empty;

            public string Url { get; set; } = string.Empty;

            public List<MessageReceiver> Receivers { get; set; } = new();
        }

        private sealed class MessageReceiver
        {
            public string Type { get; set; } = string.Empty;

            public string Id { get; set; } = string.Empty;
        }
    }
}
