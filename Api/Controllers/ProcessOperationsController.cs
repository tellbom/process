using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/processes")]
public sealed class ProcessOperationsController : ControllerBase
{
    private readonly ProcessOperationsAppService _operations;
    public ProcessOperationsController(ProcessOperationsAppService operations) => _operations = operations;

    [HttpGet("{businessId}/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ApiResult<ProcessOperationsDto>>> Get(string businessId, [FromQuery] string processInstanceId)
        => Ok(ApiResult<ProcessOperationsDto>.Ok(await _operations.GetAsync(businessId, processInstanceId)));
}
