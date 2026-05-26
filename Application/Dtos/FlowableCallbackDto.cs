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
    /// 多实例执行上下文
    /// 只有 Flowable 多实例节点（会签/多实例子任务）运行时才有稳定数据
    /// 普通节点、并行普通分支：Enabled=false，其余字段无意义
    /// </summary>
    public class MultiInstanceContext
    {
        /// <summary>
        /// 当前是否处于多实例节点执行中
        /// false = 普通节点或非多实例上下文，业务系统可忽略其余字段
        /// true  = 当前为会签/多实例子任务，nrOf* 字段有效
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// 多实例总实例数（Flowable 变量 nrOfInstances）
        /// Enabled=false 时为 0
        /// </summary>
        public int NrOfInstances { get; set; }

        /// <summary>
        /// 已完成实例数（Flowable 变量 nrOfCompletedInstances）
        /// 包含本次刚完成的实例
        /// Enabled=false 时为 0
        /// </summary>
        public int NrOfCompletedInstances { get; set; }

        /// <summary>
        /// 当前活跃实例数（Flowable 变量 nrOfActiveInstances）
        /// Enabled=false 时为 0
        /// </summary>
        public int NrOfActiveInstances { get; set; }
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

        /// <summary>
        /// 多实例执行上下文
        /// 普通节点：{ "enabled": false }
        /// 会签/多实例：{ "enabled": true, "nrOfInstances": 3, ... }
        /// 业务系统据此判断当前是否在多实例流程中，以及完成进度
        /// </summary>
        public MultiInstanceContext MultiInstance { get; set; }
            = new MultiInstanceContext { Enabled = false };
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
