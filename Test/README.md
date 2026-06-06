# Test Layer - Portal Workflow Client

这个项目是给新闻门户后端复用的流程中心客户端封装。它不引用当前流程中心的 `Api`、`Application`、`Domain`、`Infrastructure`，只按 HTTP JSON 契约调用流程中心。

## 启动并完成发起人节点

```csharp
using FlowableWrapper.Test.ProcessCenter;

var token = "<portal jwt>";
var httpClient = new HttpClient();

var processCenter = new ProcessCenterClient(
    httpClient,
    new ProcessCenterOptions("http://localhost:5012")
    {
        BearerToken = token
    });

var portalWorkflow = new PortalContentApprovalWorkflow(processCenter);

var result = await portalWorkflow.StartAndCompleteStarterAsync(
    businessId: "NEWS_20260527_001",
    starterEmployeeId: "196045",
    leaderEmployeeIds: new[] { "196001", "196002", "196003", "196004" },
    processCompletedCallbackUrl: "https://portal.example.com/api/workflow/process-callback");

Console.WriteLine(result.Start.BusinessId);
```

这会调用：

- `POST /api/processes/start`
- `POST /api/tasks/complete`

并使用门户资讯审批流程固定契约：

- `businessType = portal_content_approval`
- 发起人变量：`starterAssignee`
- 领导选人槽：`portal_leader`
- 领导推荐池角色：`portal_content_leader`

## 单独完成领导节点

```csharp
await portalWorkflow.ApproveLeaderTaskAsync(
    businessId: "NEWS_20260527_001",
    leaderTaskId: "<pending task id>",
    leaderEmployeeId: "196001");
```

驳回到发起人：

```csharp
await portalWorkflow.RejectLeaderTaskToStarterAsync(
    businessId: "NEWS_20260527_001",
    leaderTaskId: "<pending task id>",
    leaderEmployeeId: "196001",
    rejectReason: "内容需要修改");
```

## 接收节点级和流程级回调

门户后端实现 `IWorkflowCallbackHandler`：

```csharp
public sealed class PortalWorkflowCallbackHandler : IWorkflowCallbackHandler
{
    public Task OnNodeCompletedAsync(
        NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken)
    {
        // NODE_COMPLETED / MULTI_INSTANCE_COMPLETED / PARALLEL_JOIN_COMPLETED
        // 可按 payload.NodeSemantic 更新新闻审批中间状态。
        return Task.CompletedTask;
    }

    public Task OnRejectOccurredAsync(
        NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken)
    {
        // 领导驳回等场景。
        return Task.CompletedTask;
    }

    public Task OnProcessCompletedAsync(
        BusinessCallbackPayload payload,
        CancellationToken cancellationToken)
    {
        // 整个流程完成，发布新闻/公告。
        return Task.CompletedTask;
    }
}
```

Controller 示例：

```csharp
[ApiController]
[Route("api/workflow")]
public sealed class WorkflowCallbackController : ControllerBase
{
    private readonly WorkflowCallbackDispatcher _dispatcher;

    public WorkflowCallbackController(WorkflowCallbackDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    [HttpPost("node-callback")]
    public async Task<CallbackHandleResult> NodeCallback(
        [FromBody] NodeCompletedCallbackPayload payload,
        CancellationToken cancellationToken)
        => await _dispatcher.HandleNodeCallbackAsync(payload, cancellationToken);

    [HttpPost("process-callback")]
    public async Task<CallbackHandleResult> ProcessCallback(
        [FromBody] BusinessCallbackPayload payload,
        CancellationToken cancellationToken)
        => await _dispatcher.HandleProcessCompletedCallbackAsync(payload, cancellationToken);
}
```

流程中心侧：

- 节点级回调地址来自 slotConfig 每个节点的 `callbackUrl`，为空时降级到启动请求的 `callback.url`。
- 流程级完成回调地址来自启动请求的 `callback.url`。
