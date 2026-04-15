using System;
using System.Collections.Generic;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// 流程进度查询响应
    ///
    /// 数据来源说明：
    ///   - status / createdBy / createdTime / completedTime：来自 ES ProcessMetadataDocument
    ///   - currentNodes：实时查询 Flowable TaskService（执行态真相）
    ///   - auditHistory：来自 ES ProcessAuditRecord（流程中心写入）
    ///   流程中心不缓存 Flowable 执行态，当前节点信息始终从 Flowable 实时获取
    /// </summary>
    public class ProcessProgressDto
    {
        /// <summary>
        /// 业务 ID
        /// </summary>
        public string BusinessId { get; set; }

        /// <summary>
        /// Flowable 流程实例 ID
        /// </summary>
        public string ProcessInstanceId { get; set; }

        /// <summary>
        /// 流程定义 Key
        /// </summary>
        public string ProcessDefinitionKey { get; set; }

        /// <summary>
        /// 业务类型
        /// </summary>
        public string BusinessType { get; set; }

        /// <summary>
        /// 流程状态
        /// running / completed / rejected / terminated / callback_failed
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// 发起人工号
        /// </summary>
        public string CreatedBy { get; set; }

        /// <summary>
        /// 流程开始时间（UTC）
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// 流程完成时间（UTC），未完成时为 null
        /// </summary>
        public DateTime? CompletedTime { get; set; }

        /// <summary>
        /// 当前活动节点列表（实时来自 Flowable）
        /// 并行网关时可能有多个
        /// </summary>
        public List<CurrentNodeDto> CurrentNodes { get; set; }
            = new List<CurrentNodeDto>();

        /// <summary>
        /// 历史审批记录（来自 ES ProcessAuditRecord）
        /// 按操作时间升序排列
        /// </summary>
        public List<AuditRecordDto> AuditHistory { get; set; }
            = new List<AuditRecordDto>();
    }

    /// <summary>
    /// 当前活动节点信息（实时来自 Flowable）
    /// </summary>
    public class CurrentNodeDto
    {
        /// <summary>
        /// Flowable Task ID
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// 节点 Key（taskDefinitionKey）
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// 节点名称
        /// </summary>
        public string NodeName { get; set; }

        /// <summary>
        /// 节点语义（来自 ES NodeSemanticMap）
        /// </summary>
        public string NodeSemantic { get; set; }

        /// <summary>
        /// 页面编码
        /// </summary>
        public string PageCode { get; set; }

        /// <summary>
        /// 当前处理人工号（已认领则有值，候选人模式则为 null）
        /// </summary>
        public string Assignee { get; set; }

        /// <summary>
        /// 候选人工号列表（多人候选时有值）
        /// </summary>
        public List<string> CandidateUsers { get; set; } = new List<string>();

        /// <summary>
        /// 任务创建时间
        /// </summary>
        public DateTime CreateTime { get; set; }
    }

    /// <summary>
    /// 审批历史记录 DTO（来自 ES ProcessAuditRecord）
    /// </summary>
    public class AuditRecordDto
    {
        /// <summary>
        /// 节点 Key
        /// </summary>
        public string TaskDefinitionKey { get; set; }

        /// <summary>
        /// 节点语义
        /// </summary>
        public string NodeSemantic { get; set; }

        /// <summary>
        /// 节点页面编码（写入时快照，不随后续部署变更）
        /// 前端据此渲染该节点历史表单的只读视图
        /// </summary>
        public string PageCode { get; set; }

        /// <summary>
        /// 审批动作：approve / reject
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// 操作人工号
        /// </summary>
        public string OperatorId { get; set; }

        /// <summary>
        /// 审批意见
        /// </summary>
        public string Comment { get; set; }

        /// <summary>
        /// 驳回原因（action=reject 时有值）
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// 操作时间
        /// </summary>
        public DateTime OperatedAt { get; set; }

        /// <summary>
        /// 本次选人快照
        /// </summary>
        public List<SlotSelectionRecordDto> SlotSelections { get; set; }
            = new List<SlotSelectionRecordDto>();
    }

    /// <summary>
    /// 选人快照 DTO
    /// </summary>
    public class SlotSelectionRecordDto
    {
        public string SlotKey { get; set; }
        public string Label { get; set; }
        public List<string> Users { get; set; } = new List<string>();
    }
}