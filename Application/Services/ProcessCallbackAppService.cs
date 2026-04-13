using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Services
{
    /// <summary>
    /// 流程回调服务（修正版）
    ///
    /// 职责：
    ///   ✔ 接收 Flowable HTTP Task 的回调（流程走到 endEvent 后触发）
    ///   ✔ 幂等校验
    ///   ✔ 更新 ES status = completed
    ///   ✔ 转发通知业务系统
    ///
    /// 核心设计约束（修正后）：
    ///
    ///   [约束1] 流程中心不判断"完成"还是"驳回"
    ///     回调触发的唯一含义是：流程走到了 endEvent
    ///     走到 endEvent = completed，ES status 统一写 completed
    ///     业务系统如需判断审批结果（通过/驳回），自行查 ProcessAuditRecord
    ///     或读取 Flowable 流程变量（isApproved / rejectReason）
    ///
    ///   [约束2] 流程中心不干涉 Flowable 执行态
    ///     驳回时流程可能走回第一节点（仍在运行）或走到 endEvent（触发回调）
    ///     由 BPMN 设计决定，框架不关心，不预判，不提前写状态
    ///     ES status 在整个流程周转过程中始终保持 running
    ///     只有此处回调触发时才更新为 completed
    ///
    ///   [约束3] 全程同步 await，不使用 fire-and-forget
    ///     Flowable 根据 HTTP 响应码决定是否重试
    ///     必须处理完成后才返回 200，否则 Flowable 误认为成功
    ///
    ///   [约束4] 业务系统回调失败 → 返回 500 → Flowable 重试
    ///     超过重试次数 → 进 Flowable 死信队列 → 运维人工处理
    /// </summary>
    public class ProcessCallbackAppService
    {
        private readonly IElasticSearchService _esService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ProcessCallbackAppService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ProcessCallbackAppService(
            IElasticSearchService esService,
            IHttpClientFactory httpClientFactory,
            ILogger<ProcessCallbackAppService> logger)
        {
            _esService         = esService;
            _httpClientFactory = httpClientFactory;
            _logger            = logger;
        }

        // ═══════════════════════════════════════════════════════════
        // HandleFlowableCallbackAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 处理 Flowable HTTP Task 回调
        ///
        /// 触发时机：Flowable 流程走到 endEvent，HTTP ServiceTask 执行
        /// 含义：流程已自然结束，无论经过什么路径（正常通过/驳回终止）
        ///
        /// 执行步骤：
        ///   1. 参数校验
        ///   2. 查 ES 流程元数据
        ///   3. 幂等校验（已是终态直接返回 200）
        ///   4. 更新 ES status = completed，写入 completedTime
        ///   5. 若配置了业务系统回调 URL，转发通知
        ///   6. 返回成功响应（Controller 返回 200 给 Flowable）
        /// </summary>
        public async Task<FlowableCallbackResponse> HandleFlowableCallbackAsync(
            FlowableCallbackRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ProcessInstanceId))
                throw new ArgumentException("processInstanceId 不能为空");
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new ArgumentException("businessId 不能为空");

            _logger.LogInformation(
                "收到 Flowable 回调: ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
                request.ProcessInstanceId, request.BusinessId);

            // ── Step 2: 查流程元数据 ───────────────────────────────
            var metadata = await _esService.GetProcessMetadataAsync(
                request.ProcessInstanceId);

            if (metadata == null)
            {
                // ES 元数据不存在：可能是写入延迟，返回错误让 Flowable 重试
                _logger.LogWarning(
                    "未找到流程元数据，可能存在写入延迟，Flowable 将重试: " +
                    "ProcessInstanceId={ProcessInstanceId}",
                    request.ProcessInstanceId);
                throw new BusinessException(
                    $"未找到流程元数据: {request.ProcessInstanceId}",
                    "METADATA_NOT_FOUND");
            }

            // ── Step 3: 幂等校验 ───────────────────────────────────
            // 已是终态（completed / terminated）直接返回 200，不重复处理
            // 注意：running 是正常待处理状态，需要继续执行
            if (IsTerminalStatus(metadata.Status))
            {
                _logger.LogInformation(
                    "流程已处于终态 [{Status}]，忽略重复回调: " +
                    "ProcessInstanceId={ProcessInstanceId}",
                    metadata.Status, request.ProcessInstanceId);

                return new FlowableCallbackResponse
                {
                    Success = true,
                    Message = $"流程已处于终态 [{metadata.Status}]，忽略重复回调"
                };
            }

            // ── Step 4: 先转发通知业务系统 ───────────────────────
            // 只有业务系统通知成功后，才允许将 ES 状态更新为 completed。
            // 否则一旦先写 completed，后续 Flowable 重试会被幂等直接拦截，
            // 导致业务系统永远收不到成功通知。
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

            // ── Step 5: 业务系统通知成功后，再更新 ES status = completed ──
            // 回调触发的唯一含义是：流程走到了 endEvent，即 completed。
            // 不区分"正常完成"还是"驳回终止"——那是业务语义，不是框架关心的。
            // 业务系统如需判断审批结果，查 ProcessAuditRecord 的 action 字段。
            await _esService.UpdateProcessStatusAsync(
                request.ProcessInstanceId,
                "completed",
                DateTime.UtcNow);

            _logger.LogInformation(
                "回调处理完成，ES 流程状态更新为 completed: ProcessInstanceId={ProcessInstanceId}",
                request.ProcessInstanceId);

            return new FlowableCallbackResponse
            {
                Success = true,
                Message = "回调处理成功"
            };
        }

        // ═══════════════════════════════════════════════════════════
        // 私有辅助方法
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 判断是否已是终态
        /// terminated：管理员手动终止
        /// completed ：流程自然走完（包含任何路径）
        /// callback_failed 不是终态，允许重新处理
        /// </summary>
        private static bool IsTerminalStatus(string status)
            => status is "completed" or "terminated";

        /// <summary>
        /// 转发通知业务系统
        ///
        /// 通知内容：
        ///   - businessId / processInstanceId / status = completed
        ///   - 不包含审批结果（业务系统自行查 ProcessAuditRecord 判断）
        ///
        /// 失败处理：
        ///   抛出异常 → Controller 返回 500 → Flowable 重试
        ///   ⚠ 注意：此时 ES status 已更新为 completed
        ///           下次重试时幂等检查会直接返回 200，业务系统通知不会重试
        ///           这是一个已知的权衡：ES 状态更新 vs 业务系统通知的原子性
        ///           后续可引入独立的业务通知重试机制解耦
        /// </summary>
        private async Task CallBusinessSystemAsync(ProcessMetadataDocument metadata)
        {
            var callbackUrl = metadata.Callback.Url;

            _logger.LogInformation(
                "转发通知业务系统: BusinessId={BusinessId}, Url={Url}",
                metadata.BusinessId, callbackUrl);

            var payload = new BusinessCallbackPayload
            {
                BusinessId           = metadata.BusinessId,
                ProcessInstanceId    = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType         = metadata.BusinessType,
                // 框架只通知 completed，不区分路径
                // 业务系统通过查 ProcessAuditRecord 判断审批结果
                Status               = "completed",
                CompletedTime        = DateTime.UtcNow
            };

            try
            {
                var httpClient = _httpClientFactory.CreateClient("BusinessCallback");
                httpClient.Timeout = TimeSpan.FromSeconds(
                    metadata.Callback.TimeoutSeconds > 0
                        ? metadata.Callback.TimeoutSeconds
                        : 30);

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post, callbackUrl);

                // 自定义请求头（如 Authorization）
                if (metadata.Callback.Headers != null)
                {
                    foreach (var header in metadata.Callback.Headers)
                        httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                httpRequest.Content = JsonContent.Create(payload, options: JsonOptions);

                var response = await httpClient.SendAsync(httpRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "业务系统通知成功: BusinessId={BusinessId}, StatusCode={StatusCode}",
                        metadata.BusinessId, (int)response.StatusCode);
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "业务系统通知失败（非 2xx）: BusinessId={BusinessId}, " +
                    "StatusCode={StatusCode}, Response={Response}",
                    metadata.BusinessId, (int)response.StatusCode, responseBody);

                // 标记 callback_failed 便于监控告警
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
                    metadata.BusinessId, callbackUrl);

                await UpdateCallbackFailedSafeAsync(metadata.ProcessInstanceId);

                throw new BusinessException(
                    $"业务系统通知异常: {ex.Message}",
                    "BUSINESS_CALLBACK_EXCEPTION");
            }
        }

        /// <summary>
        /// 安全标记 callback_failed，失败只记日志不抛异常
        /// </summary>
        private async Task UpdateCallbackFailedSafeAsync(string processInstanceId)
        {
            try
            {
                await _esService.UpdateProcessStatusAsync(
                    processInstanceId, "callback_failed");

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
    }

    /// <summary>
    /// 转发给业务系统的通知体
    ///
    /// 设计说明：
    ///   Status 固定为 completed，不区分审批路径
    ///   业务系统需要判断"通过/驳回/退回"时，查询以下接口：
    ///     GET /api/processes/{businessId}/progress → auditHistory
    ///   auditHistory 中最后一条记录的 action 即为最终审批动作
    /// </summary>
    public class BusinessCallbackPayload
    {
        public string BusinessId           { get; set; }
        public string ProcessInstanceId    { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string BusinessType         { get; set; }
        public string Status               { get; set; }
        public DateTime CompletedTime      { get; set; }
    }
}
