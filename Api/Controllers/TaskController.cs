using System.Threading.Tasks;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Api.Controllers
{
    /// <summary>
    /// 任务执行接口
    /// </summary>
    [ApiController]
    [Route("api/tasks")]
    public class TaskController : ControllerBase
    {
        private readonly TaskExecutionAppService _taskService;
        private readonly ILogger<TaskController> _logger;

        public TaskController(
            TaskExecutionAppService taskService,
            ILogger<TaskController> logger)
        {
            _taskService = taskService;
            _logger = logger;
        }

        /// <summary>
        /// 完成（审批）任务
        /// POST /api/tasks/complete
        ///
        /// 并行场景说明：
        ///   同一用户同时是多个并行节点的处理人时，必须在 request.taskId 中
        ///   传入从待办列表获取的 taskId，否则框架只能取第一个匹配任务（不确定）
        /// </summary>
        [HttpPost("complete")]
        public async Task<ActionResult<ApiResult<CompleteTaskResponse>>> CompleteTask(
            [FromBody] CompleteTaskRequest request)
        {
            var result = await _taskService.CompleteTaskAsync(request);
            return Ok(ApiResult<CompleteTaskResponse>.Ok(result));
        }

        /// <summary>
        /// 查询待办任务列表
        /// GET /api/tasks/pending?employeeId=EMP_001&amp;pageIndex=1&amp;pageSize=20
        ///
        /// 返回的每条记录包含 taskId，前端在 complete 时可传入以明确指定任务
        /// </summary>
        [HttpGet("pending")]
        public async Task<ActionResult<ApiResult<PendingTaskPageResult>>> GetPendingTasks(
            [FromQuery] GetPendingTasksRequest request)
        {
            var result = await _taskService.GetPendingTasksAsync(request);
            return Ok(ApiResult<PendingTaskPageResult>.Ok(result));
        }

        /// <summary>
        /// 转派任务
        /// POST /api/tasks/reassign
        ///
        /// taskId 说明：
        ///   - 并行场景：必须传 taskId 指定转派哪个任务
        ///   - 单节点场景：不传 taskId，转派该流程下所有待办任务
        /// </summary>
        [HttpPost("reassign")]
        public async Task<ActionResult<ApiResult<object>>> ReassignTask(
            [FromBody] ReassignTaskRequest request)
        {
            await _taskService.ReassignTaskAsync(request);
            return Ok(ApiResult.OkEmpty("转派成功"));
        }
    }
}
