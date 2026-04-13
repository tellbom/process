using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Flowable
{
    /// <summary>
    /// Flowable运行时服务实现
    /// </summary>
    public class FlowableRuntimeServiceImpl : IFlowableRuntimeService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FlowableRuntimeServiceImpl> _logger;
        private readonly string _baseUrl;

        public FlowableRuntimeServiceImpl(
            HttpClient httpClient,
            IOptions<FlowableOptions> options,
            ILogger<FlowableRuntimeServiceImpl> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _baseUrl = options.Value.BaseUrl.TrimEnd('/');

            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes(
                    $"{options.Value.Username}:{options.Value.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        public async Task<FlowableProcessInstance> StartProcessInstanceByKeyAsync(
            string processDefinitionKey,
            string businessKey,
            Dictionary<string, object> variables)
        {
            var url = $"{_baseUrl}/runtime/process-instances";

            var request = new
            {
                processDefinitionKey,
                businessKey,
                variables = BuildVariableList(variables)
            };

            var response = await _httpClient.PostAsJsonAsync(url, request);
            await EnsureSuccessAsync(response, "runtime/process-instances");

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();

            return new FlowableProcessInstance
            {
                Id = result.GetProperty("id").GetString(),
                ProcessDefinitionId = result.GetProperty("processDefinitionId").GetString(),
                ProcessDefinitionKey = result.GetProperty("processDefinitionKey").GetString(),
                BusinessKey = result.TryGetProperty("businessKey", out var bk)
                                        ? bk.GetString() : null,
                IsEnded = result.GetProperty("ended").GetBoolean()
            };
        }

        public async Task<FlowableProcessInstance> GetProcessInstanceAsync(
            string processInstanceId)
        {
            var url = $"{_baseUrl}/runtime/process-instances/{processInstanceId}";
            var response = await _httpClient.GetAsync(url);
            await EnsureSuccessAsync(response, $"runtime/process-instances/{processInstanceId}");

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();

            return new FlowableProcessInstance
            {
                Id = result.GetProperty("id").GetString(),
                ProcessDefinitionId = result.GetProperty("processDefinitionId").GetString(),
                ProcessDefinitionKey = result.GetProperty("processDefinitionKey").GetString(),
                BusinessKey = result.TryGetProperty("businessKey", out var bk)
                                        ? bk.GetString() : null,
                IsEnded = result.GetProperty("ended").GetBoolean()
            };
        }

        public async Task DeleteProcessInstanceAsync(
            string processInstanceId,
            string deleteReason)
        {
            var url = $"{_baseUrl}/runtime/process-instances/{processInstanceId}";
            if (!string.IsNullOrWhiteSpace(deleteReason))
                url += $"?deleteReason={Uri.EscapeDataString(deleteReason)}";

            var response = await _httpClient.DeleteAsync(url);
            await EnsureSuccessAsync(response,
                $"runtime/process-instances/{processInstanceId}");
        }

        /// <summary>
        /// 强制迁移流程节点（驳回跳转）
        /// POST /runtime/process-instances/{id}/change-state
        /// </summary>
        public async Task ChangeActivityStateAsync(
            string processInstanceId,
            List<string> cancelActivityIds,
            string startActivityId)
        {
            var url = $"{_baseUrl}/runtime/process-instances/{processInstanceId}/change-state";

            var body = new
            {
                cancelActivityIds = cancelActivityIds.ToArray(),
                startActivityIds = new[] { startActivityId }
            };

            var response = await _httpClient.PostAsJsonAsync(url, body);
            await EnsureSuccessAsync(response,
                $"runtime/process-instances/{processInstanceId}/change-state");

            _logger.LogInformation(
                "节点跳转成功: ProcessInstanceId={ProcessInstanceId}, " +
                "取消=[{CancelNodes}] → 启动={StartNode}",
                processInstanceId,
                string.Join("、", cancelActivityIds),
                startActivityId);
        }

        // ── 私有辅助 ──────────────────────────────────────────────

        /// <summary>
        /// 构建 Flowable variables 数组
        ///
        /// 修复：ASP.NET Core 从 JSON 请求体反序列化 Dictionary&lt;string,object&gt; 时，
        /// 所有值都是 JsonElement 类型，不是 int/bool/string 等基元类型。
        /// 必须拆包后正确推断 Flowable 变量类型，否则 Flowable 报
        /// "Converter can only convert strings"
        /// </summary>
        private static List<object> BuildVariableList(Dictionary<string, object> variables)
        {
            var list = new List<object>();
            if (variables == null) return list;

            foreach (var kv in variables)
            {
                var (type, value) = ResolveVariable(kv.Value);
                list.Add(new { name = kv.Key, value, type });
            }

            return list;
        }

        private static (string type, object value) ResolveVariable(object raw)
        {
            if (raw is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.True or
                    JsonValueKind.False => ("boolean", (object)je.GetBoolean()),
                    JsonValueKind.Number when je.TryGetInt64(out var l) => ("integer", (object)l),
                    JsonValueKind.Number when je.TryGetDouble(out var d) => ("double", (object)d),
                    JsonValueKind.String => ("string", (object)(je.GetString() ?? "")),
                    JsonValueKind.Array or
                    JsonValueKind.Object => ("json", (object)je.GetRawText()),
                    _ => ("string", (object)je.ToString())
                };
            }

            return raw switch
            {
                bool b => ("boolean", (object)b),
                int i => ("integer", (object)(long)i),
                long l => ("integer", (object)l),
                double d => ("double", (object)d),
                float f => ("double", (object)(double)f),
                decimal dec => ("double", (object)(double)dec),
                System.Collections.IList => ("json", (object)JsonSerializer.Serialize(raw)),
                string s => ("string", (object)s),
                _ => ("string", (object)(raw?.ToString() ?? ""))
            };
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            string path)
        {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync();
            throw new FlowableApiException(
                $"Flowable API 调用失败: {(int)response.StatusCode} {path} — {body}", (int)response.StatusCode);
        }
    }
}
