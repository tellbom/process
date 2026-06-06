using System.Text.Json.Serialization;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class StartProcessRequest
{
    [JsonPropertyName("businessType")]
    public string BusinessType { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("initialSlotSelections")]
    public List<SlotSelection> InitialSlotSelections { get; set; } = new();

    [JsonPropertyName("businessVariables")]
    public Dictionary<string, object> BusinessVariables { get; set; } = new();

    [JsonPropertyName("callback")]
    public CallbackConfig? Callback { get; set; }

    [JsonPropertyName("assigneeContract")]
    public AssigneeContract? AssigneeContract { get; set; }
}

public sealed class StartProcessResponse
{
    [JsonPropertyName("processInstanceId")]
    public string ProcessInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("firstTaskId")]
    public string FirstTaskId { get; set; } = string.Empty;

    [JsonPropertyName("firstNodeSemantic")]
    public string FirstNodeSemantic { get; set; } = string.Empty;

    [JsonPropertyName("firstPageCode")]
    public string FirstPageCode { get; set; } = string.Empty;
}

public sealed class CompleteTaskRequest
{
    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("employeeId")]
    public string EmployeeId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public int Action { get; set; } = ApprovalAction.Approve;

    [JsonPropertyName("rejectCode")]
    public string? RejectCode { get; set; }

    [JsonPropertyName("rejectReason")]
    public string? RejectReason { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("nextSlotSelections")]
    public List<SlotSelection> NextSlotSelections { get; set; } = new();

    [JsonPropertyName("businessVariables")]
    public Dictionary<string, object> BusinessVariables { get; set; } = new();
}

public sealed class CompleteTaskResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public static class ApprovalAction
{
    public const int Approve = 1;
    public const int Reject = 2;
}

public sealed class SlotSelection
{
    [JsonPropertyName("slotKey")]
    public string SlotKey { get; set; } = string.Empty;

    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = new();
}

public sealed class AssigneeContract
{
    [JsonPropertyName("roles")]
    public List<RoleAssignment> Roles { get; set; } = new();
}

public sealed class RoleAssignment
{
    [JsonPropertyName("roleKey")]
    public string RoleKey { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "single";

    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = new();
}

public sealed class CallbackConfig
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 3;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();
}

public sealed class PendingTaskPageResult
{
    [JsonPropertyName("items")]
    public List<PendingTaskDto> Items { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pageIndex")]
    public int PageIndex { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

public sealed class PendingTaskDto
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("taskName")]
    public string TaskName { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("businessType")]
    public string BusinessType { get; set; } = string.Empty;

    [JsonPropertyName("nodeSemantic")]
    public string NodeSemantic { get; set; } = string.Empty;

    [JsonPropertyName("roleKey")]
    public string RoleKey { get; set; } = string.Empty;

    [JsonPropertyName("pageCode")]
    public string PageCode { get; set; } = string.Empty;

    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; set; }

    [JsonPropertyName("requiredSlots")]
    public List<SlotDefinition> RequiredSlots { get; set; } = new();

    [JsonPropertyName("canReject")]
    public bool CanReject { get; set; }

    [JsonPropertyName("rejectOptions")]
    public List<RejectOption> RejectOptions { get; set; } = new();

    [JsonPropertyName("slotRecommendedUsers")]
    public Dictionary<string, List<string>> SlotRecommendedUsers { get; set; } = new();

    [JsonPropertyName("restrictToRecommended")]
    public Dictionary<string, bool> RestrictToRecommended { get; set; } = new();
}

public sealed class SlotDefinition
{
    [JsonPropertyName("slotKey")]
    public string SlotKey { get; set; } = string.Empty;

    [JsonPropertyName("roleKey")]
    public string RoleKey { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "single";

    [JsonPropertyName("variableName")]
    public string VariableName { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("conditionalOn")]
    public string? ConditionalOn { get; set; }

    [JsonPropertyName("restrictToRecommended")]
    public bool RestrictToRecommended { get; set; }
}

public sealed class RejectOption
{
    [JsonPropertyName("rejectCode")]
    public string RejectCode { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
