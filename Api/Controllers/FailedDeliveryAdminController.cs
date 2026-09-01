using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/admin/failed-deliveries")]
public sealed class FailedDeliveryAdminController : ControllerBase
{
    private readonly FailedDeliveryAppService _service;

    public FailedDeliveryAdminController(FailedDeliveryAppService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResult<FailedDeliveryPageDto>>> Query(
        [FromQuery] FailedDeliveryQuery query)
        => Ok(ApiResult<FailedDeliveryPageDto>.Ok(
            await _service.QueryAsync(query)));

    [HttpGet("{deliveryId}")]
    public async Task<ActionResult<ApiResult<FailedDeliveryDto>>> Get(
        string deliveryId)
        => Ok(ApiResult<FailedDeliveryDto>.Ok(
            await _service.GetAsync(deliveryId)));

    [HttpPost("{deliveryId}/retry")]
    public async Task<ActionResult<ApiResult<FailedDeliveryActionResultDto>>> Retry(
        string deliveryId,
        [FromBody] FailedDeliveryActionRequest? request)
        => Accepted(ApiResult<FailedDeliveryActionResultDto>.Ok(
            await _service.RetryAsync(deliveryId, request?.Reason)));

    [HttpPost("{deliveryId}/terminate-process")]
    public async Task<ActionResult<ApiResult<FailedDeliveryActionResultDto>>> Terminate(
        string deliveryId,
        [FromBody] FailedDeliveryActionRequest request)
        => Ok(ApiResult<FailedDeliveryActionResultDto>.Ok(
            await _service.TerminateProcessAsync(deliveryId, request?.Reason)));
}
