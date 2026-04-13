using System.Collections.Generic;
using System.Threading.Tasks;
using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Api.Controllers
{
    /// <summary>
    /// 流程查询接口（只读）
    /// </summary>
    [ApiController]
    [Route("api/processes")]
    public class ProcessQueryController : ControllerBase
    {
        private readonly ProcessQueryAppService _queryService;
        private readonly ILogger<ProcessQueryController> _logger;

        public ProcessQueryController(
            ProcessQueryAppService queryService,
            ILogger<ProcessQueryController> logger)
        {
            _queryService = queryService;
            _logger       = logger;
        }

        /// <summary>
        /// 查询流程进度（含当前节点 + 审批历史）
        /// GET /api/processes/{businessId}/progress
        ///
        /// 数据来源：
        ///   基本信息    → ES ProcessMetadataDocument
        ///   当前节点    → Flowable 实时查询
        ///   审批历史    → ES ProcessAuditRecord
        ///
        /// 流程已结束时 currentNodes 为空列表
        /// </summary>
        [HttpGet("{businessId}/progress")]
        public async Task<ActionResult<ApiResult<ProcessProgressDto>>> GetProgress(
            string businessId)
        {
            var result = await _queryService.GetProcessProgressAsync(businessId);
            return Ok(ApiResult<ProcessProgressDto>.Ok(result));
        }

        /// <summary>
        /// 查询审批历史（轻量，不含当前节点）
        /// GET /api/processes/{businessId}/audit-history
        ///
        /// 适用场景：
        ///   - 只需要历史记录，不需要当前节点信息
        ///   - 业务系统判断审批结果（查最后一条 action）
        /// </summary>
        [HttpGet("{businessId}/audit-history")]
        public async Task<ActionResult<ApiResult<List<AuditRecordDto>>>> GetAuditHistory(
            string businessId)
        {
            var result = await _queryService.GetAuditHistoryAsync(
                new AuditHistoryRequest { BusinessId = businessId });
            return Ok(ApiResult<List<AuditRecordDto>>.Ok(result));
        }

        /// <summary>
        /// 按 businessId 查单条流程状态（极轻量）
        /// GET /api/processes/{businessId}/status
        ///
        /// 适用场景：
        ///   业务系统轮询流程是否完成时使用此接口
        ///   只返回 status / createdTime / completedTime，不查 Flowable 当前任务
        /// </summary>
        [HttpGet("{businessId}/status")]
        public async Task<ActionResult<ApiResult<ProcessListItemDto>>> GetStatus(
            string businessId)
        {
            var result = await _queryService.GetProcessStatusByBusinessIdAsync(businessId);
            return Ok(ApiResult<ProcessListItemDto>.Ok(result));
        }

        /// <summary>
        /// 分页查询流程列表
        /// GET /api/processes?businessType=xxx&amp;status=running&amp;pageIndex=1&amp;pageSize=20
        ///
        /// 过滤参数（均可选）：
        ///   businessType  : 按业务类型过滤
        ///   status        : running / completed / terminated / callback_failed
        ///   createdBy     : 按发起人工号过滤
        ///   createdTimeFrom / createdTimeTo : 时间范围（UTC ISO 格式）
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<ApiResult<ProcessListResult>>> GetProcessList(
            [FromQuery] ProcessListRequest request)
        {
            var result = await _queryService.GetProcessListAsync(request);
            return Ok(ApiResult<ProcessListResult>.Ok(result));
        }
    }
}
