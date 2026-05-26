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

        /// <summary>
        /// 推荐处理人快照，Key = roleKey，Value = 推荐人员列表。
        /// 来源：StartProcessRequest.AssigneeContract 展开（直接按 roleKey 存储）。
        ///
        /// roleKey 表示"谁来处理当前节点"；slotKey 表示"当前节点完成时为下一节点选谁"。
        /// 两者是不同主体，不能把当前节点 roleKey 的推荐人写入当前节点的 slot。
        ///
        /// 节点推进时前端读取此快照初始化选人区，最终由用户通过 NextSlotSelections 确认提交。
        /// 不参与 Flowable 变量投影，不影响执行路径。
        /// </summary>
        public Dictionary<string, List<string>> RecommendedAssigneesSnapshot { get; set; }
            = new Dictionary<string, List<string>>();

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

        /// <summary>
        /// 该节点绑定的业务角色 Key，对应 AssigneeContract.Roles[].RoleKey。
        /// </summary>
        public string RoleKey { get; set; }

        /// <summary>
        /// 处理人模式：single = 单人，multiple = 多人。
        /// </summary>
        public string AssigneeMode { get; set; }

        /// <summary>
        /// 节点级回调 URL。节点完成后流程中心固定以 POST 方式发送 NodeCompletedCallbackPayload。
        /// 为空时降级使用流程实例 metadata.Callback.Url；两者均为空则跳过节点回调。
        /// </summary>
        public string CallbackUrl { get; set; }
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
        /// <summary>
        /// 选人槽位 Key，同一流程全局唯一。
        /// 前端按此 key 提交 nextSlotSelections，后端按此 key 返回 slotRecommendedUsers。
        /// </summary>
        public string SlotKey { get; set; }

        /// <summary>
        /// 该 slot 的候选人推荐池来源角色 Key。
        /// 与 NodeSemanticInfo.RoleKey 含义不同：
        /// - NodeSemanticInfo.RoleKey：当前节点的处理人角色（谁来审批这个节点）。
        /// - SlotDefinition.RoleKey：该 slot 选人时应从哪个角色池取推荐人，
        ///   即 RecommendedAssigneesSnapshot[slot.RoleKey]。
        /// 同一节点的多个 slot 可以引用不同的 roleKey。
        /// </summary>
        public string RoleKey { get; set; }

        /// <summary>前端展示标签</summary>
        public string Label { get; set; }

        /// <summary>single = 单人，multiple = 多人</summary>
        public string Mode { get; set; }

        /// <summary>
        /// 对应的 Flowable 流程变量名。
        /// 任务完成时后端将选人结果写入此变量，Flowable 引擎据此绑定下一节点 assignee。
        /// 不暴露为前端提交 key，前端只感知 slotKey。
        /// </summary>
        public string VariableName { get; set; }

        /// <summary>是否必填</summary>
        public bool Required { get; set; } = true;

        /// <summary>条件表达式，满足时才需要填写，如 needFeedback=true</summary>
        public string ConditionalOn { get; set; }

        /// <summary>
        /// 是否限制只能从推荐范围内选人。后端不强拦截，仅在审计时记录是否越界。
        /// </summary>
        public bool RestrictToRecommended { get; set; } = false;
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

        /// <summary>
        /// 节点页面编码快照，写入时从 NodeSemanticInfo.PageCode 取值固化
        /// 记录操作发生时的表单组件路径，不随后续部署变更而改变
        /// </summary>
        public string PageCode { get; set; }

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

        /// <summary>
        /// 本次提交中是否有人员超出推荐范围；null 表示不适用。
        /// </summary>
        public bool? HasOutOfRecommendedRange { get; set; }

        /// <summary>
        /// 本次完成任务时各 slot 的推荐人快照，Key = slotKey。
        /// 数据来自 roleKey 维度的 RecommendedAssigneesSnapshot；仅当 slotKey 与目标 roleKey 对应时记录。
        /// </summary>
        public Dictionary<string, List<string>> RecommendedUsersSnapshot { get; set; }
            = new Dictionary<string, List<string>>();

        /// <summary>
        /// 各 slot 的 RestrictToRecommended 配置值快照，Key = slotKey。
        /// </summary>
        public Dictionary<string, bool> RestrictToRecommendedSnapshot { get; set; }
            = new Dictionary<string, bool>();
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
