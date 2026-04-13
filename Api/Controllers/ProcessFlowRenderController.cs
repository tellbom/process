using System.Threading.Tasks;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Api.Controllers
{
    /// <summary>
    /// 流程图渲染数据接口
    /// </summary>
    [ApiController]
    [Route("api/processes")]
    public class ProcessFlowRenderController : ControllerBase
    {
        private readonly ProcessFlowRenderAppService _renderService;
        private readonly ILogger<ProcessFlowRenderController> _logger;

        public ProcessFlowRenderController(
            ProcessFlowRenderAppService renderService,
            ILogger<ProcessFlowRenderController> logger)
        {
            _renderService = renderService;
            _logger        = logger;
        }

        /// <summary>
        /// 获取流程图渲染数据
        /// GET /api/processes/{businessId}/flow-render
        ///
        /// 返回 Flowgraph.vue 所需的完整数据，直接赋给 :data prop
        ///
        /// 使用场景：
        ///   1. MyTodo.vue 的"查看进度"Popover（对应 apiGetFlowRender）
        ///   2. MyApplication.vue 的"查看进度"Popover
        ///   3. TaskApproveDrawer 内嵌的流程图
        ///   4. ApplicationViewDrawer 的流程图 Tab
        /// </summary>
        [HttpGet("{businessId}/flow-render")]
        public async Task<ActionResult<ApiResult<ProcessFlowRenderDto>>> GetFlowRender(
            string businessId)
        {
            var result = await _renderService.GetFlowRenderAsync(businessId);
            return Ok(ApiResult<ProcessFlowRenderDto>.Ok(result));
        }
    }
}
