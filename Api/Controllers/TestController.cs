using FlowableWrapper.Application.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/test")]
public sealed class TestController : ControllerBase
{
    private static readonly ConcurrentQueue<TestCallbackRecord> CallbackRecords = new();

    [HttpGet]
    public IActionResult Get() => Ok(new { ok = true });

    [HttpDelete("callbacks")]
    public IActionResult ClearCallbacks()
    {
        while (CallbackRecords.TryDequeue(out _)) { }
        return Ok(new { ok = true, count = 0 });
    }

    [HttpGet("callbacks")]
    public IActionResult GetCallbacks([FromQuery] string? businessId = null)
    {
        var records = CallbackRecords.ToArray()
            .Where(r => string.IsNullOrWhiteSpace(businessId)
                || string.Equals(r.BusinessId, businessId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.ReceivedAt)
            .ToList();

        return Ok(new
        {
            ok = true,
            count = records.Count,
            records
        });
    }

    [AllowAnonymous]
    [HttpPost("node-callback")]
    public IActionResult NodeCallback([FromBody] NodeCompletedCallbackPayload request)
    {
        CallbackRecords.Enqueue(TestCallbackRecord.FromNode(request));

        return Ok(new
        {
            ok = true,
            callbackType = request.CallbackType,
            received = request
        });
    }

    [AllowAnonymous]
    [HttpPost("process-callback")]
    public IActionResult ProcessCallback([FromBody] BusinessCallbackPayload request)
    {
        CallbackRecords.Enqueue(TestCallbackRecord.FromProcess(request));

        return Ok(new
        {
            ok = true,
            callbackType = "process_completed",
            received = request
        });
    }

    public sealed class TestCallbackRecord
    {
        public string Kind { get; set; } = string.Empty;
        public string? BusinessId { get; set; }
        public string? ProcessInstanceId { get; set; }
        public string? CallbackType { get; set; }
        public string? TaskDefinitionKey { get; set; }
        public string? NodeSemantic { get; set; }
        public DateTime ReceivedAt { get; set; }
        public object? Payload { get; set; }

        public static TestCallbackRecord FromNode(NodeCompletedCallbackPayload payload)
            => new()
            {
                Kind = "node",
                BusinessId = payload.BusinessId,
                ProcessInstanceId = payload.ProcessInstanceId,
                CallbackType = payload.CallbackType,
                TaskDefinitionKey = payload.TaskDefinitionKey,
                NodeSemantic = payload.NodeSemantic,
                ReceivedAt = DateTime.UtcNow,
                Payload = payload
            };

        public static TestCallbackRecord FromProcess(BusinessCallbackPayload payload)
            => new()
            {
                Kind = "process",
                BusinessId = payload.BusinessId,
                ProcessInstanceId = payload.ProcessInstanceId,
                CallbackType = "PROCESS_COMPLETED",
                ReceivedAt = DateTime.UtcNow,
                Payload = payload
            };
    }
}
