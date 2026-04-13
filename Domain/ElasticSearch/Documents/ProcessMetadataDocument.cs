using System;
using System.Collections.Generic;

namespace FlowableWrapper.Domain.ElasticSearch
{
    /// <summary>
    /// ES 流程实例元数据文档
    /// 文档 ID = ProcessInstanceId
    /// </summary>
    public class ProcessMetadataDocument
    {
        public string Id { get; set; }
        public string ProcessInstanceId { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string BusinessId { get; set; }
        public string BusinessType { get; set; }

        /// <summary>running / completed / terminated / callback_failed</summary>
        public string Status { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public CallbackMetadata Callback { get; set; }

        public Dictionary<string, NodeSemanticInfo> NodeSemanticMap { get; set; }

        public ProcessMetadataDocument() { }
    }

    /// <summary>
    /// 业务系统回调配置
    /// </summary>
    public class CallbackMetadata
    {
        public string Url { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public int RetryCount { get; set; } = 3;
        public Dictionary<string, string> Headers { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════
    // 节点语义（随 BPMN 部署写入 ES，存储在独立的语义索引中）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 节点语义信息
    /// 描述每个 userTask 的业务语义、驳回能力和选人契约
    /// </summary>
    public class NodeSemanticInfo
    {
        /// <summary>节点 Key，对应 BPMN taskDefinitionKey</summary>
        public string TaskDefinitionKey { get; set; }

        /// <summary>业务语义，前端通过此值路由表单组件</summary>
        public string NodeSemantic { get; set; }

        /// <summary>页面编码，前端通过 COMPONENT_REGISTRY[pageCode] 找到 Vue 组件</summary>
        public string PageCode { get; set; }

        /// <summary>是否不可撤回节点，通过此节点后流程不可退回</summary>
        public bool IsConvergencePoint { get; set; }

        /// <summary>
        /// 是否为发起人节点
        /// 一个流程只能有一个，是所有驳回的最终落点
        /// </summary>
        public bool IsStarterNode { get; set; }

        /// <summary>
        /// 当前节点是否具备驳回能力（源节点维度）
        /// true = 前端显示驳回按钮
        /// </summary>
        public bool CanReject { get; set; }

        /// <summary>
        /// 当前节点支持的驳回选项列表
        /// CanReject=true 时有值
        /// </summary>
        public List<RejectOption> RejectOptions { get; set; } = new();

        /// <summary>
        /// 当前节点是否可作为驳回目标（目标节点维度）
        /// true = 其他节点可以驳回跳回此处
        /// </summary>
        public bool IsRejectTarget { get; set; }

        /// <summary>
        /// 驳回目标代码，同一流程定义内全局唯一
        /// IsRejectTarget=true 时必填
        /// 示例：TO_STARTER / TO_DEPT_HEAD / TO_DEPT_APPROVE
        /// </summary>
        public string RejectCode { get; set; }

        /// <summary>
        /// Slot 定义列表
        /// 当前节点完成时为下一节点选人的契约
        /// </summary>
        public List<SlotDefinition> Slots { get; set; } = new();
    }

    /// <summary>
    /// 驳回选项（描述从当前节点可以驳回到哪里）
    /// </summary>
    public class RejectOption
    {
        /// <summary>驳回目标代码，对应目标节点的 RejectCode</summary>
        public string RejectCode { get; set; }
        /// <summary>前端展示标签，如"退回发起人"</summary>
        public string Label { get; set; }
        /// <summary>说明文字（可选）</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Slot 定义（选人契约）
    /// 约定当前节点完成时需要为下一节点传入哪些处理人
    /// </summary>
    public class SlotDefinition
    {
        /// <summary>选人槽位 Key，同一流程全局唯一</summary>
        public string SlotKey { get; set; }
        /// <summary>前端展示标签</summary>
        public string Label { get; set; }
        /// <summary>single = 单人，multiple = 多人</summary>
        public string Mode { get; set; }
        /// <summary>对应的 Flowable 变量名</summary>
        public string VariableName { get; set; }
        /// <summary>是否必填</summary>
        public bool Required { get; set; } = true;
        /// <summary>条件表达式，满足时才需要填写，如 needFeedback=true</summary>
        public string ConditionalOn { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // ES 流程定义语义文档（独立索引，与流程实例无关）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 流程定义语义文档
    /// 索引：flowable-process-definition-semantic
    /// 文档 ID = ProcessDefinitionKey
    /// 部署 BPMN 时写入，所有同 Key 的流程实例共用
    /// </summary>
    public class ProcessDefinitionSemanticDocument
    {
        public string Id { get; set; }
        public string ProcessDefinitionKey { get; set; }

        /// <summary>Key = taskDefinitionKey</summary>
        public Dictionary<string, NodeSemanticInfo> NodeSemanticMap { get; set; }
            = new();

        public DateTime LastUpdatedTime { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    // 审计记录
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 审批审计记录
    /// 索引：flowable-audit-records
    /// 每次 CompleteTask 写入一条
    /// </summary>
    public class ProcessAuditRecord
    {
        public string Id { get; set; }
        public string ProcessInstanceId { get; set; }
        public string BusinessId { get; set; }
        public string BusinessType { get; set; }
        public string TaskId { get; set; }
        public string TaskDefinitionKey { get; set; }
        public string NodeSemantic { get; set; }

        /// <summary>approve / reject</summary>
        public string Action { get; set; }
        public string OperatorId { get; set; }
        public string Comment { get; set; }
        public string RejectReason { get; set; }

        /// <summary>驳回模式代码，action=reject 时有值</summary>
        public string RejectCode { get; set; }

        /// <summary>
        /// 驳回目标节点 Key，action=reject 时写入
        /// 对应目标节点的 TaskDefinitionKey（BPMN userTask id）
        /// ES 索引为动态映射，存量文档该字段为 null，由 BuildRejectHistory 兼容处理
        /// </summary>
        public string RejectTargetNodeKey { get; set; }

        public DateTime OperatedAt { get; set; }

        public List<SlotSelectionRecord> SlotSelections { get; set; } = new();
    }

    /// <summary>
    /// 审计记录中的选人快照
    /// </summary>
    public class SlotSelectionRecord
    {
        public string SlotKey { get; set; }
        public string Label { get; set; }
        public List<string> Users { get; set; } = new();
    }
}