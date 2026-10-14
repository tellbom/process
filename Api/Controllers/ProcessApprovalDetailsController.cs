using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/processes/instances")]
public sealed class ProcessApprovalDetailsController : ControllerBase
{
    private readonly ProcessApprovalDetailsAppService _service;

    public ProcessApprovalDetailsController(ProcessApprovalDetailsAppService service)
        => _service = service;

    /// <summary>业务系统按流程实例查询审签详情，无需用户 JWT。应通过内网访问边界限制调用方。</summary>
    [AllowAnonymous]
    [HttpGet("{processInstanceId}/approval-details")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ApiResult<ProcessApprovalDetailsDto>>> GetApprovalDetails(
        string processInstanceId)
        => Ok(ApiResult<ProcessApprovalDetailsDto>.Ok(
            await _service.GetApprovalDetailsAsync(processInstanceId)));
}
