using FlowableWrapper.Application.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/test")]
public sealed class TestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { ok = true });

    [HttpPost("node-callback")]
    public IActionResult NodeCallback([FromBody] NodeCompletedCallbackPayload request)
    {
        return Ok(new
        {
            ok = true,
            callbackType = request.CallbackType,
            received = request
        });
    }

    [HttpPost("process-callback")]
    public IActionResult ProcessCallback([FromBody] BusinessCallbackPayload request)
    {
        return Ok(new
        {
            ok = true,
            callbackType = "process_completed",
            received = request
        });
    }
}
