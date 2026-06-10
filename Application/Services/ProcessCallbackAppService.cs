using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Services
{
    /// <summary>
    /// Handles Flowable HTTP ServiceTask callbacks.
    /// Node callbacks trust Flowable progression as the completion proof and do not query Flowable again.
    /// </summary>
    public class ProcessCallbackAppService
    {
        private readonly IElasticSearchService _esService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IProcessSlotConfigProvider _slotConfigProvider;
        private readonly ILogger<ProcessCallbackAppService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ProcessCallbackAppService(
            IElasticSearchService esService,
            IHttpClientFactory httpClientFactory,
            IProcessSlotConfigProvider slotConfigProvider,
            ILogger<ProcessCallbackAppService> logger)
        {
            _esService = esService;
            _httpClientFactory = httpClientFactory;
            _slotConfigProvider = slotConfigProvider;
            _logger = logger;
        }

        public async Task<FlowableCallbackResponse> HandleFlowableCallbackAsync(
            FlowableCallbackRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ProcessInstanceId))
                throw new ArgumentException("processInstanceId 不能为空");
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new ArgumentException("businessId 不能为空");

            var callbackType = request.Variables
                ?.GetValueOrDefault("callbackType")?.ToString();

            _logger.LogInformation(
                "收到 Flowable 回调: ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}, CallbackType={CallbackType}",
                request.ProcessInstanceId,
                request.BusinessId,
                callbackType ?? "(process_end)");

            if (IsNodeCallbackType(callbackType))
                return await HandleNodeCallbackAsync(request, callbackType);

            return await HandleProcessEndCallbackAsync(request);
        }

        private async Task<FlowableCallbackResponse> HandleNodeCallbackAsync(
            FlowableCallbackRequest request,
            string callbackType)
        {
            var taskDefinitionKey = request.Variables
                ?.GetValueOrDefault("callbackNodeKey")?.ToString();

            if (string.IsNullOrWhiteSpace(taskDefinitionKey))
            {
                _logger.LogWarning(
                    "[{CallbackType}] 缺少 callbackNodeKey，已跳过。ProcessInstanceId={ProcessInstanceId}",
                    callbackType,
                    request.ProcessInstanceId);
                return OkResponse($"{callbackType}: callbackNodeKey 为空，跳过");
            }

            var metadata = await _esService.GetProcessMetadataAsync(request.ProcessInstanceId);
            var callbackUrl = await ResolveNodeCallbackUrlAsync(
                taskDefinitionKey,
                metadata?.ProcessDefinitionKey ?? request.ProcessDefinitionKey,
                metadata?.Callback?.Url);

            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                _logger.LogDebug(
                    "[{CallbackType}] 节点 [{NodeKey}] 未配置回调 URL，已跳过。ProcessInstanceId={ProcessInstanceId}",
                    callbackType,
                    taskDefinitionKey,
                    request.ProcessInstanceId);
                return OkResponse($"{callbackType}: 未配置回调 URL，跳过");
            }

            var context = await BuildNodeCallbackContextAsync(
                request.ProcessInstanceId,
                request.BusinessId,
                request.ProcessDefinitionKey,
                taskDefinitionKey,
                metadata);

            var payload = new NodeCompletedCallbackPayload
            {
                BusinessId = request.BusinessId,
                ProcessInstanceId = request.ProcessInstanceId,
                ProcessDefinitionKey = context.ProcessDefinitionKey,
                BusinessType = context.BusinessType,
                CallbackType = callbackType.ToUpperInvariant(),
                TaskDefinitionKey = taskDefinitionKey,
                NodeSemantic = context.NodeSemantic,
                LastAuditRecord = context.LastAuditRecord,
                TriggeredAt = DateTime.UtcNow
            };

            await PostNodeCallbackSafeAsync(callbackUrl, payload, callbackType, metadata);

            return OkResponse($"{callbackType}: 节点 [{taskDefinitionKey}] 回调已发送");
        }

        /// <summary>
        /// 从 Flowable 流程变量中构建多实例上下文
        /// nrOfInstances / nrOfCompletedInstances / nrOfActiveInstances
        /// 由 Flowable 引擎在多实例节点执行时自动写入，不是流程中心计算的
        /// 非多实例节点这些变量不存在，返回 Enabled=false
        /// </summary>
        private static MultiInstanceContext BuildMultiInstanceContext(
            Dictionary<string, object> variables)
        {
            if (variables == null || !variables.Any())
                return new MultiInstanceContext { Enabled = false };

            if (!variables.TryGetValue("nrOfInstances", out var nrOfInstancesObj)
                || !int.TryParse(nrOfInstancesObj?.ToString(), out var nrOfInstances)
                || nrOfInstances <= 0)
                return new MultiInstanceContext { Enabled = false };

            int.TryParse(
                variables.GetValueOrDefault("nrOfCompletedInstances")?.ToString() ?? "0",
                out var nrOfCompleted);
            int.TryParse(
                variables.GetValueOrDefault("nrOfActiveInstances")?.ToString() ?? "0",
                out var nrOfActive);

            return new MultiInstanceContext
            {
                Enabled                = true,
                NrOfInstances          = nrOfInstances,
                NrOfCompletedInstances = nrOfCompleted,
                NrOfActiveInstances    = nrOfActive
            };
        }

        /// <summary>
        /// 主动发送节点完成回调（不依赖 Flowable HTTP ServiceTask）
        ///
        /// 触发条件（优先级）：
        ///   1. slotConfig 当前节点 callbackUrl 有有效 URL → 发节点级 URL
        ///   2. slotConfig 当前节点 callbackUrl 显式 null/空 → 跳过，不降级
        ///   3. slotConfig 未声明 callbackUrl → metadata.Callback.Url 流程级兼容降级
        ///
        /// 时序保证：
        ///   调用方已先执行 WriteAuditRecordSafeAsync（Step 6）
        ///   BuildNodeCallbackContextAsync 查 ES 时能拿到完整 lastAuditRecord
        ///
        /// multiInstance 数据来源：
        ///   从 Flowable 流程变量中读取 nrOfInstances / nrOfCompletedInstances / nrOfActiveInstances
        ///   这些变量由 Flowable 引擎写入，不是流程中心计算的
        ///   非多实例节点这些变量不存在，MultiInstanceContext.Enabled = false
        ///
        /// 失败处理：
        ///   网络失败或业务系统非 2xx → 只记 Error 日志，不抛异常，不阻塞主流程
        /// </summary>
        public async Task SendNodeCompletedCallbackSafeAsync(
            ProcessMetadataDocument metadata,
            string taskDefinitionKey,
            Dictionary<string, object> processVariables = null)
        {
            if (metadata == null)
            {
                _logger.LogWarning("节点完成通知：流程元数据为空，已跳过");
                return;
            }

            var callbackUrl = await ResolveNodeCallbackUrlAsync(
                taskDefinitionKey,
                metadata.ProcessDefinitionKey,
                metadata.Callback?.Url);

            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                _logger.LogDebug(
                    "节点 [{NodeKey}] 无回调 URL，跳过主动通知。BusinessId={BusinessId}",
                    taskDefinitionKey, metadata.BusinessId);
                return;
            }

            var context = await BuildNodeCallbackContextAsync(
                metadata.ProcessInstanceId,
                metadata.BusinessId,
                metadata.ProcessDefinitionKey,
                taskDefinitionKey,
                metadata);

            var multiInstance = BuildMultiInstanceContext(processVariables);

            var payload = new NodeCompletedCallbackPayload
            {
                BusinessId           = metadata.BusinessId,
                ProcessInstanceId    = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType         = metadata.BusinessType,
                CallbackType         = FlowableCallbackTypes.NodeCompleted,
                TaskDefinitionKey    = taskDefinitionKey,
                NodeSemantic         = context.NodeSemantic,
                LastAuditRecord      = context.LastAuditRecord,
                MultiInstance        = multiInstance,
                TriggeredAt          = DateTime.UtcNow
            };

            _logger.LogInformation(
                "发送节点完成主动回调: NodeKey={NodeKey}, Url={Url}, " +
                "MultiInstance={Enabled}, BusinessId={BusinessId}",
                taskDefinitionKey, callbackUrl,
                multiInstance.Enabled, metadata.BusinessId);

            await PostNodeCallbackSafeAsync(
                callbackUrl, payload,
                FlowableCallbackTypes.NodeCompleted,
                metadata);
        }

        /// <summary>
        /// Sends a reject notification to the business callback endpoint.
        /// The notification is emitted by the process center after Flowable activity-state jump succeeds.
        /// </summary>
        public async Task SendRejectCallbackSafeAsync(
            ProcessMetadataDocument metadata,
            string rejectNodeKey,
            string rejectTargetNodeKey,
            AuditRecordSnapshot auditSnapshot)
        {
            if (metadata == null)
            {
                _logger.LogWarning("驳回通知：流程元数据为空，已跳过");
                return;
            }

            var callbackUrl = await ResolveNodeCallbackUrlAsync(
                rejectNodeKey,
                metadata.ProcessDefinitionKey,
                metadata.Callback?.Url);

            if (string.IsNullOrWhiteSpace(callbackUrl))
            {
                _logger.LogDebug(
                    "驳回通知：未配置回调 URL，跳过。BusinessId={BusinessId}",
                    metadata.BusinessId);
                return;
            }

            var nodeSemantic = string.Empty;
            try
            {
                var semanticMap = await _slotConfigProvider
                    .GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey);
                if (semanticMap.TryGetValue(rejectNodeKey, out var nodeInfo))
                    nodeSemantic = nodeInfo?.NodeSemantic ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reject callback failed to resolve node semantic. BusinessId={BusinessId}, NodeKey={NodeKey}",
                    metadata.BusinessId,
                    rejectNodeKey);
            }

            if (auditSnapshot != null)
            {
                auditSnapshot.RejectTargetNodeKey = rejectTargetNodeKey;
            }

            var payload = new NodeCompletedCallbackPayload
            {
                BusinessId = metadata.BusinessId,
                ProcessInstanceId = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType = metadata.BusinessType,
                CallbackType = FlowableCallbackTypes.RejectOccurred,
                TaskDefinitionKey = rejectNodeKey,
                NodeSemantic = nodeSemantic,
                RejectTargetNodeKey = rejectTargetNodeKey,
                LastAuditRecord = auditSnapshot,
                TriggeredAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "发送驳回通知: BusinessId={BusinessId}, RejectNode={RejectNode}, TargetNode={TargetNode}, Url={Url}",
                metadata.BusinessId,
                rejectNodeKey,
                rejectTargetNodeKey,
                callbackUrl);

            await PostNodeCallbackSafeAsync(
                callbackUrl,
                payload,
                FlowableCallbackTypes.RejectOccurred,
                metadata);
        }

        private async Task<FlowableCallbackResponse> HandleProcessEndCallbackAsync(
            FlowableCallbackRequest request)
        {
            var metadata = await _esService.GetProcessMetadataAsync(
                request.ProcessInstanceId);

            if (metadata == null)
            {
                _logger.LogWarning(
                    "未找到流程元数据，可能存在写入延迟，Flowable 将重试: ProcessInstanceId={ProcessInstanceId}",
                    request.ProcessInstanceId);
                throw new BusinessException(
                    $"未找到流程元数据: {request.ProcessInstanceId}",
                    "METADATA_NOT_FOUND");
            }

            if (IsTerminalStatus(metadata.Status))
            {
                _logger.LogInformation(
                    "流程已处于终态 [{Status}]，忽略重复回调: ProcessInstanceId={ProcessInstanceId}",
                    metadata.Status,
                    request.ProcessInstanceId);
                return OkResponse($"流程已处于终态 [{metadata.Status}]，忽略重复回调");
            }

            if (metadata.Callback != null
                && !string.IsNullOrWhiteSpace(metadata.Callback.Url))
            {
                await CallBusinessSystemAsync(metadata);
            }
            else
            {
                _logger.LogInformation(
                    "未配置业务系统回调 URL，跳过转发: BusinessId={BusinessId}",
                    metadata.BusinessId);
            }

            await _esService.UpdateProcessStatusAsync(
                request.ProcessInstanceId,
                "completed",
                DateTime.UtcNow);

            _logger.LogInformation(
                "流程结束回调处理完成，ES 状态更新为 completed: ProcessInstanceId={ProcessInstanceId}",
                request.ProcessInstanceId);

            return OkResponse("回调处理成功");
        }

        private async Task<NodeCallbackContext> BuildNodeCallbackContextAsync(
            string processInstanceId,
            string businessId,
            string processDefinitionKey,
            string taskDefinitionKey,
            ProcessMetadataDocument? preloadedMetadata = null)
        {
            var context = new NodeCallbackContext
            {
                ProcessDefinitionKey = processDefinitionKey
            };

            try
            {
                var metadata = preloadedMetadata
                    ?? await _esService.GetProcessMetadataAsync(processInstanceId);
                if (metadata != null)
                {
                    context.ProcessDefinitionKey = string.IsNullOrWhiteSpace(processDefinitionKey)
                        ? metadata.ProcessDefinitionKey
                        : processDefinitionKey;
                    context.BusinessType = metadata.BusinessType;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "节点回调读取流程元数据失败，将继续发送基础上下文: ProcessInstanceId={ProcessInstanceId}",
                    processInstanceId);
            }

            try
            {
                var auditRecords = await _esService
                    .QueryAuditRecordsByBusinessIdAsync(businessId);

                var record = auditRecords?
                    .Where(r => string.Equals(
                        r.TaskDefinitionKey,
                        taskDefinitionKey,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.OperatedAt)
                    .FirstOrDefault();

                if (record != null)
                {
                    context.NodeSemantic = record.NodeSemantic;
                    context.LastAuditRecord = new AuditRecordSnapshot
                    {
                        Action = record.Action ?? string.Empty,
                        OperatorId = record.OperatorId ?? string.Empty,
                        Comment = record.Comment ?? string.Empty,
                        RejectReason = record.RejectReason ?? string.Empty,
                        RejectCode = record.RejectCode ?? string.Empty,
                        RejectTargetNodeKey = record.RejectTargetNodeKey ?? string.Empty,
                        OperatedAt = record.OperatedAt,
                        SlotSelections = NormalizeSlotSelections(record.SlotSelections)
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "组装节点回调审计上下文失败，将使用空审计上下文继续发送: BusinessId={BusinessId}, NodeKey={NodeKey}",
                    businessId,
                    taskDefinitionKey);
            }

            return context;
        }

        private static List<SlotSelectionRecord> NormalizeSlotSelections(
            List<SlotSelectionRecord>? slotSelections)
        {
            if (slotSelections == null || !slotSelections.Any())
                return new List<SlotSelectionRecord>();

            return slotSelections.Select(s => new SlotSelectionRecord
            {
                SlotKey = s?.SlotKey ?? string.Empty,
                Label = s?.Label ?? string.Empty,
                Users = s?.Users ?? new List<string>()
            }).ToList();
        }

        private async Task<string?> ResolveNodeCallbackUrlAsync(
            string taskDefinitionKey,
            string? processDefinitionKey,
            string? processCallbackUrl)
        {
            if (!string.IsNullOrWhiteSpace(processDefinitionKey))
            {
                try
                {
                    var semanticMap = await _slotConfigProvider
                        .GetNodeSemanticMapAsync(processDefinitionKey);

                    if (semanticMap != null
                        && semanticMap.TryGetValue(taskDefinitionKey, out var nodeInfo))
                    {
                        if (!string.IsNullOrWhiteSpace(nodeInfo.CallbackUrl))
                        {
                            _logger.LogDebug(
                                "使用节点级回调 URL: NodeKey={NodeKey}, Url={Url}",
                                taskDefinitionKey,
                                nodeInfo.CallbackUrl);
                            return nodeInfo.CallbackUrl;
                        }

                        if (nodeInfo.CallbackUrlSpecified)
                        {
                            _logger.LogDebug(
                                "节点 [{NodeKey}] 显式禁用回调，跳过流程级 URL 降级",
                                taskDefinitionKey);
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "读取节点级回调 URL 失败，将降级到流程级 URL: NodeKey={NodeKey}",
                        taskDefinitionKey);
                }
            }

            if (!string.IsNullOrWhiteSpace(processCallbackUrl))
            {
                _logger.LogDebug(
                    "降级使用流程级回调 URL: NodeKey={NodeKey}, Url={Url}",
                    taskDefinitionKey,
                    processCallbackUrl);
                return processCallbackUrl;
            }

            return null;
        }

        private async Task PostNodeCallbackSafeAsync(
            string url,
            NodeCompletedCallbackPayload payload,
            string callbackType,
            ProcessMetadataDocument? metadata = null)
        {
            try
            {
                var httpClient = _httpClientFactory.CreateClient("BusinessCallback");
                var timeoutSeconds = metadata?.Callback?.TimeoutSeconds > 0
                    ? metadata.Callback.TimeoutSeconds
                    : 30;
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);

                if (metadata?.Callback?.Headers != null)
                {
                    foreach (var header in metadata.Callback.Headers)
                        httpRequest.Headers.TryAddWithoutValidation(
                            header.Key,
                            header.Value);
                }

                httpRequest.Content = JsonContent.Create(payload, options: JsonOptions);

                var response = await httpClient.SendAsync(httpRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "[{CallbackType}] 节点回调成功: NodeKey={NodeKey}, StatusCode={StatusCode}",
                        callbackType,
                        payload.TaskDefinitionKey,
                        (int)response.StatusCode);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[{CallbackType}] 节点回调失败（非 2xx）: NodeKey={NodeKey}, StatusCode={StatusCode}, Response={Response}",
                    callbackType,
                    payload.TaskDefinitionKey,
                    (int)response.StatusCode,
                    body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{CallbackType}] 节点回调异常: NodeKey={NodeKey}, Url={Url}",
                    callbackType,
                    payload.TaskDefinitionKey,
                    url);
            }
        }

        private async Task CallBusinessSystemAsync(ProcessMetadataDocument metadata)
        {
            var callbackUrl = metadata.Callback.Url;

            _logger.LogInformation(
                "转发流程结束通知: BusinessId={BusinessId}, Url={Url}",
                metadata.BusinessId,
                callbackUrl);

            var payload = new BusinessCallbackPayload
            {
                BusinessId = metadata.BusinessId,
                ProcessInstanceId = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType = metadata.BusinessType,
                Status = "completed",
                CompletedTime = DateTime.UtcNow
            };

            try
            {
                var httpClient = _httpClientFactory.CreateClient("BusinessCallback");
                httpClient.Timeout = TimeSpan.FromSeconds(
                    metadata.Callback.TimeoutSeconds > 0
                        ? metadata.Callback.TimeoutSeconds
                        : 30);

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    callbackUrl);

                if (metadata.Callback.Headers != null)
                {
                    foreach (var header in metadata.Callback.Headers)
                        httpRequest.Headers.TryAddWithoutValidation(
                            header.Key,
                            header.Value);
                }

                httpRequest.Content = JsonContent.Create(payload, options: JsonOptions);

                var response = await httpClient.SendAsync(httpRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "业务系统通知成功: BusinessId={BusinessId}, StatusCode={StatusCode}",
                        metadata.BusinessId,
                        (int)response.StatusCode);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "业务系统通知失败（非 2xx）: BusinessId={BusinessId}, StatusCode={StatusCode}, Response={Response}",
                    metadata.BusinessId,
                    (int)response.StatusCode,
                    body);

                await UpdateCallbackFailedSafeAsync(metadata.ProcessInstanceId);

                throw new BusinessException(
                    $"业务系统通知失败: HTTP {(int)response.StatusCode}",
                    "BUSINESS_CALLBACK_FAILED");
            }
            catch (BusinessException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "业务系统通知异常: BusinessId={BusinessId}, Url={Url}",
                    metadata.BusinessId,
                    callbackUrl);

                await UpdateCallbackFailedSafeAsync(metadata.ProcessInstanceId);

                throw new BusinessException(
                    $"业务系统通知异常: {ex.Message}",
                    "BUSINESS_CALLBACK_EXCEPTION");
            }
        }

        // NODE_COMPLETED 已改为由 TaskExecutionAppService.CompleteTaskAsync Step 8 主动推送，
        // 不再经由 Flowable HTTP ServiceTask 回调到 /api/callback/flowable 路径。
        // MULTI_INSTANCE_COMPLETED 和 PARALLEL_JOIN_COMPLETED 仍保留 Flowable HTTP ServiceTask 路径。
        private static bool IsNodeCallbackType(string? callbackType)
            => string.Equals(
                   callbackType,
                   FlowableCallbackTypes.MultiInstanceCompleted,
                   StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                   callbackType,
                   FlowableCallbackTypes.ParallelJoinCompleted,
                   StringComparison.OrdinalIgnoreCase);

        private static bool IsTerminalStatus(string status)
            => status is "completed" or "terminated";

        private static FlowableCallbackResponse OkResponse(string message)
            => new FlowableCallbackResponse { Success = true, Message = message };

        private async Task UpdateCallbackFailedSafeAsync(string processInstanceId)
        {
            try
            {
                await _esService.UpdateProcessStatusAsync(
                    processInstanceId,
                    "callback_failed");

                _logger.LogWarning(
                    "流程标记为 callback_failed: ProcessInstanceId={ProcessInstanceId}",
                    processInstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "标记 callback_failed 失败: ProcessInstanceId={ProcessInstanceId}",
                    processInstanceId);
            }
        }

        private class NodeCallbackContext
        {
            public string ProcessDefinitionKey { get; set; } = string.Empty;
            public string BusinessType { get; set; } = string.Empty;
            public string NodeSemantic { get; set; } = string.Empty;
            public AuditRecordSnapshot? LastAuditRecord { get; set; }
        }
    }
}
