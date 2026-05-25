using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FlowableWrapper.Domain.ElasticSearch;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// Flowable HTTP ServiceTask callback request.
    /// </summary>
    public class FlowableCallbackRequest
    {
        [Required(ErrorMessage = "processInstanceId 不能为空")]
        public string ProcessInstanceId { get; set; } = string.Empty;

        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; } = string.Empty;

        public string ProcessDefinitionKey { get; set; } = string.Empty;

        /// <summary>
        /// Flowable variables passed through by the HTTP ServiceTask.
        /// callbackType decides the processing path:
        /// NODE_COMPLETED / MULTI_INSTANCE_COMPLETED / PARALLEL_JOIN_COMPLETED.
        /// Missing or unknown values use the process-end callback path.
        /// </summary>
        public Dictionary<string, object> Variables { get; set; }
            = new Dictionary<string, object>();
    }

    public static class FlowableCallbackTypes
    {
        public const string NodeCompleted = "NODE_COMPLETED";
        public const string MultiInstanceCompleted = "MULTI_INSTANCE_COMPLETED";
        public const string ParallelJoinCompleted = "PARALLEL_JOIN_COMPLETED";
        public const string RejectOccurred = "REJECT_OCCURRED";
    }

    public class FlowableCallbackResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Payload sent from the process center to a business system for all node-level callbacks.
    /// </summary>
    public class NodeCompletedCallbackPayload
    {
        public string BusinessId { get; set; } = string.Empty;
        public string ProcessInstanceId { get; set; } = string.Empty;
        public string ProcessDefinitionKey { get; set; } = string.Empty;
        public string BusinessType { get; set; } = string.Empty;
        public string CallbackType { get; set; } = string.Empty;
        public string TaskDefinitionKey { get; set; } = string.Empty;
        public string NodeSemantic { get; set; } = string.Empty;
        public string RejectTargetNodeKey { get; set; } = string.Empty;
        public AuditRecordSnapshot? LastAuditRecord { get; set; }
        public DateTime TriggeredAt { get; set; }
    }

    public class AuditRecordSnapshot
    {
        public string Action { get; set; } = string.Empty;
        public string OperatorId { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string RejectReason { get; set; } = string.Empty;
        public string RejectCode { get; set; } = string.Empty;
        public string RejectTargetNodeKey { get; set; } = string.Empty;
        public DateTime OperatedAt { get; set; }
        public List<SlotSelectionRecord> SlotSelections { get; set; }
            = new List<SlotSelectionRecord>();
    }

    /// <summary>
    /// Payload sent from the process center to a business system when the whole process ends.
    /// </summary>
    public class BusinessCallbackPayload
    {
        public string BusinessId { get; set; } = string.Empty;
        public string ProcessInstanceId { get; set; } = string.Empty;
        public string ProcessDefinitionKey { get; set; } = string.Empty;
        public string BusinessType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CompletedTime { get; set; }
    }
}
