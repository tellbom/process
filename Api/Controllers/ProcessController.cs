using System.Threading.Tasks;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Api.Controllers
{
    /// <summary>
    /// 流程生命周期接口
    /// </summary>
    [ApiController]
    [Route("api/processes")]
    public class ProcessController : ControllerBase
    {
        private readonly ProcessLifecycleAppService _lifecycleService;
        private readonly ILogger<ProcessController> _logger;

        public ProcessController(
            ProcessLifecycleAppService lifecycleService,
            ILogger<ProcessController> logger)
        {
            _lifecycleService = lifecycleService;
            _logger           = logger;
        }

        /// <summary>
        /// 启动流程
        /// POST /api/processes/start
        ///
        /// 调用示例：
        /// {
        ///   "businessType": "personnel_selection_approval",
        ///   "businessId": "SELECTION_2024_001",
        ///   "initialSlotSelections": [
        ///     { "slotKey": "group_leader", "users": ["EMP_001"] }
        ///   ],
        ///   "businessVariables": { "title": "2024年第一批选调" },
        ///   "callback": { "url": "https://biz-system/api/workflow/callback" }
        /// }
        /// </summary>
        [HttpPost("start")]
        public async Task<ActionResult<ApiResult<StartProcessResponse>>> StartProcess(
            [FromBody] StartProcessRequest request)
        {
            var result = await _lifecycleService.StartProcessAsync(request);
            return Ok(ApiResult<StartProcessResponse>.Ok(result));
        }

        /// <summary>
        /// 终止流程（管理员操作）
        /// POST /api/processes/terminate
        /// </summary>
        [HttpPost("terminate")]
        public async Task<ActionResult<ApiResult<object>>> TerminateProcess(
            [FromBody] TerminateProcessRequest request)
        {
            await _lifecycleService.TerminateProcessAsync(request);
            return Ok(ApiResult.OkEmpty("流程已终止"));
        }
    }
}
