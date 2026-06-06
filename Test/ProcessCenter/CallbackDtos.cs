using System.Text.Json.Serialization;

namespace FlowableWrapper.Test.ProcessCenter;

public static class WorkflowCallbackTypes
{
    public const string NodeCompleted = "NODE_COMPLETED";
    public const string MultiInstanceCompleted = "MULTI_INSTANCE_COMPLETED";
    public const string ParallelJoinCompleted = "PARALLEL_JOIN_COMPLETED";
    public const string RejectOccurred = "REJECT_OCCURRED";
}

public sealed class NodeCompletedCallbackPayload
{
    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("processInstanceId")]
    public string ProcessInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("processDefinitionKey")]
    public string ProcessDefinitionKey { get; set; } = string.Empty;

    [JsonPropertyName("businessType")]
    public string BusinessType { get; set; } = string.Empty;

    [JsonPropertyName("callbackType")]
    public string CallbackType { get; set; } = string.Empty;

    [JsonPropertyName("taskDefinitionKey")]
    public string TaskDefinitionKey { get; set; } = string.Empty;

    [JsonPropertyName("nodeSemantic")]
    public string NodeSemantic { get; set; } = string.Empty;

    [JsonPropertyName("rejectTargetNodeKey")]
    public string RejectTargetNodeKey { get; set; } = string.Empty;

    [JsonPropertyName("lastAuditRecord")]
    public AuditRecordSnapshot? LastAuditRecord { get; set; }

    [JsonPropertyName("triggeredAt")]
    public DateTime TriggeredAt { get; set; }

    [JsonPropertyName("multiInstance")]
    public MultiInstanceContext MultiInstance { get; set; } = new();
}

public sealed class BusinessCallbackPayload
{
    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = string.Empty;

    [JsonPropertyName("processInstanceId")]
    public string ProcessInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("processDefinitionKey")]
    public string ProcessDefinitionKey { get; set; } = string.Empty;

    [JsonPropertyName("businessType")]
    public string BusinessType { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("completedTime")]
    public DateTime CompletedTime { get; set; }
}

public sealed class MultiInstanceContext
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("nrOfInstances")]
    public int NrOfInstances { get; set; }

    [JsonPropertyName("nrOfCompletedInstances")]
    public int NrOfCompletedInstances { get; set; }

    [JsonPropertyName("nrOfActiveInstances")]
    public int NrOfActiveInstances { get; set; }
}

public sealed class AuditRecordSnapshot
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("operatorId")]
    public string OperatorId { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("rejectReason")]
    public string RejectReason { get; set; } = string.Empty;

    [JsonPropertyName("rejectCode")]
    public string RejectCode { get; set; } = string.Empty;

    [JsonPropertyName("rejectTargetNodeKey")]
    public string RejectTargetNodeKey { get; set; } = string.Empty;

    [JsonPropertyName("operatedAt")]
    public DateTime OperatedAt { get; set; }

    [JsonPropertyName("slotSelections")]
    public List<SlotSelectionRecord> SlotSelections { get; set; } = new();
}

public sealed class SlotSelectionRecord
{
    [JsonPropertyName("slotKey")]
    public string SlotKey { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("users")]
    public List<string> Users { get; set; } = new();
}

public sealed class CallbackHandleResult
{
    public bool Success { get; init; } = true;
    public string Message { get; init; } = string.Empty;

    public static CallbackHandleResult Ok(string message)
        => new() { Success = true, Message = message };
}
