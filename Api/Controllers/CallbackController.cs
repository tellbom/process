using System;
using System.Threading.Tasks;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Api.Controllers
{
    /// <summary>
    /// Flowable HTTP Task 回调接收接口
    ///
    /// 此接口仅由 Flowable 引擎调用，不由业务系统或前端直接调用。
    /// URL 配置：appsettings.json → Flowable.FrameworkCallbackUrl
    ///
    /// Flowable HTTP Task 重试行为：
    ///   2xx  → 成功，Flowable 流程正常结束
    ///   5xx  → 失败，Flowable 触发重试（按 BPMN HTTP Task 配置的重试次数）
    ///   超限 → 进 Flowable 死信队列，运维人工处理
    ///
    /// ⚠ 注意：此 Controller 不使用 GlobalExceptionFilter 的 400 转换
    ///         所有异常必须返回 5xx，否则 Flowable 不会触发重试
    /// </summary>
    [ApiController]
    [Route("api/callback")]
    public class CallbackController : ControllerBase
    {
        private readonly ProcessCallbackAppService _callbackService;
        private readonly ILogger<CallbackController> _logger;

        public CallbackController(
            ProcessCallbackAppService callbackService,
            ILogger<CallbackController> logger)
        {
            _callbackService = callbackService;
            _logger          = logger;
        }

        /// <summary>
        /// 接收 Flowable HTTP Task 回调
        /// POST /api/callback/flowable
        ///
        /// BPMN 中 HTTP Task 配置参考：
        ///   requestMethod  : POST
        ///   requestUrl     : ${frameworkCallbackUrl}
        ///   requestHeaders : Content-Type: application/json
        ///   requestBody    : {
        ///                      "processInstanceId":"${execution.processInstanceId}",
        ///                      "businessId":"${businessId}",
        ///                      "processDefinitionKey":"${processDefinitionKey}"
        ///                    }
        /// </summary>
        [HttpPost("flowable")]
        public async Task<ActionResult<FlowableCallbackResponse>> HandleFlowableCallback(
            [FromBody] FlowableCallbackRequest request)
        {
            try
            {
                var result = await _callbackService.HandleFlowableCallbackAsync(request);

                // 返回 200：Flowable 认为回调处理成功，流程正常结束
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Flowable 回调处理失败，返回 500 触发 Flowable 重试: " +
                    "ProcessInstanceId={ProcessInstanceId}",
                    request?.ProcessInstanceId);

                // 返回 500：触发 Flowable HTTP Task 重试机制
                // ⚠ 不能返回 400（Flowable 认为客户端错误，不重试）
                return StatusCode(500, new FlowableCallbackResponse
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }
    }
}
