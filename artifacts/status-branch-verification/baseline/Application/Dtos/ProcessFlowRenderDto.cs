using System;
using System.Collections.Generic;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// 流程图渲染数据 DTO
    /// 专为 Flowgraph.vue 设计，包含节点状态染色、边状态、审计记录等所有渲染所需数据
    ///
    /// 数据来源：
    ///   节点列表（含坐标）  → 解析 BPMN XML
    ///   节点状态染色        → Flowable 历史任务 + 当前活动任务
    ///   completedRecords   → ES ProcessAuditRecord
    ///   activeTasks        → Flowable TaskService 实时查
    ///   rejectHistory      → ES ProcessAuditRecord（action=reject）
    ///
    /// 前端使用方式：
    ///   GET /api/processes/{businessId}/flow-render
    ///   → 赋给 Flowgraph.vue 的 :data prop
    /// </summary>
    public class ProcessFlowRenderDto
    {
        public string BusinessId           { get; set; }
        public string ProcessInstanceId    { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string BusinessType         { get; set; }
        public string Status               { get; set; }
        public string CreatedBy            { get; set; }
        public DateTime CreatedTime        { get; set; }
        public DateTime? CompletedTime     { get; set; }

        /// <summary>
        /// 是否有驳回历史（控制前端"驳回轨迹"区域的显示）
        /// </summary>
        public bool HasRejectHistory { get; set; }

        /// <summary>
        /// 已走过的节点 ID 列表（用于边的状态染色）
        /// </summary>
        public List<string> WalkedNodeIds { get; set; } = new();

        /// <summary>
        /// 所有节点（含网关、事件节点）
        /// 坐标从 BPMN DI 段解析，无 DI 时为 null（前端 Flowgraph.vue 走 dagre 自动布局）
        /// </summary>
        public List<FlowNodeDto> Nodes { get; set; } = new();

        /// <summary>
        /// 所有边（含驳回回退边）
        /// </summary>
        public List<FlowEdgeDto> Edges { get; set; } = new();

        /// <summary>
        /// 当前活动任务（实时来自 Flowable，对应节点 state=active）
        /// </summary>
        public List<ActiveTaskRenderDto> ActiveTasks { get; set; } = new();

        /// <summary>
        /// 已完成的审批记录（来自 ES ProcessAuditRecord）
        /// 对应节点悬浮卡中的"历史审批"展示
        /// </summary>
        public List<CompletedRecordRenderDto> CompletedRecords { get; set; } = new();

        /// <summary>
        /// 驳回轨迹列表（来自 ES ProcessAuditRecord，action=reject）
        /// </summary>
        public List<RejectHistoryRenderDto> RejectHistory { get; set; } = new();
    }

    /// <summary>
    /// 流程图节点 DTO
    /// 与 Flowgraph.vue 中 nodes 数组的每个元素完全对齐
    /// </summary>
    public class FlowNodeDto
    {
        /// <summary>
        /// 节点 ID（对应 BPMN 的 id 属性，即 taskDefinitionKey）
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 显示标签（对应 BPMN 的 name 属性）
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// 节点类型
        /// 枚举值：userTask / serviceTask / startEvent / endEvent /
        ///         parallelGateway / exclusiveGateway / inclusiveGateway
        /// </summary>
        public string NodeType { get; set; }

        /// <summary>
        /// 节点状态（用于染色）
        /// 枚举值：completed（已完成）/ active（审批中）/
        ///         rejected（有驳回）/ pending（待流转）/ skipped（已跳过）
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// 该节点的处理人工号列表
        /// completed 节点：审批过的人；active 节点：当前处理人
        /// </summary>
        public List<string> Assignees { get; set; } = new();

        /// <summary>
        /// 节点完成时间（UTC），active/pending 节点为 null
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// 是否多实例（会签/或签）节点
        /// </summary>
        public bool IsMultiInstance { get; set; }

        /// <summary>
        /// X 坐标（来自 BPMN DI 段，无 DI 则为 null，前端走 dagre 布局）
        /// </summary>
        public double? X { get; set; }

        /// <summary>
        /// Y 坐标
        /// </summary>
        public double? Y { get; set; }

        /// <summary>
        /// 节点宽度（来自 BPMN DI，无则 null，前端用默认值）
        /// </summary>
        public double? Width { get; set; }

        /// <summary>
        /// 节点高度
        /// </summary>
        public double? Height { get; set; }
    }

    /// <summary>
    /// 流程图边 DTO
    /// </summary>
    public class FlowEdgeDto
    {
        public string Id     { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }

        /// <summary>
        /// 边状态（用于染色）
        /// 枚举值：walked（已走过）/ active（正在流转）/ pending（未走）/ rejected（驳回边）
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// 边标签（排他网关分支标签，来自 BPMN sequenceFlow 的 branchLabel 扩展属性）
        /// </summary>
        public string Label { get; set; }
    }

    /// <summary>
    /// 当前活动任务渲染 DTO（实时来自 Flowable）
    /// 对应 Flowgraph.vue 悬浮卡中"当前处理人"展示
    /// </summary>
    public class ActiveTaskRenderDto
    {
        public string TaskId         { get; set; }
        public string NodeId         { get; set; }
        public string NodeName       { get; set; }
        public string Assignee       { get; set; }
        public List<string> CandidateUsers { get; set; } = new();
        public DateTime CreatedAt    { get; set; }

        /// <summary>
        /// 等待时长（秒），用于展示"已等待 X 天"
        /// </summary>
        public long WaitingSeconds   { get; set; }
    }

    /// <summary>
    /// 已完成审批记录渲染 DTO（来自 ES ProcessAuditRecord）
    /// 对应 Flowgraph.vue 悬浮卡中"历史审批"展示
    /// </summary>
    public class CompletedRecordRenderDto
    {
        public string TaskId       { get; set; }
        public string NodeId       { get; set; }
        public string NodeName     { get; set; }
        public string OperatorId   { get; set; }
        public DateTime StartTime  { get; set; }
        public DateTime EndTime    { get; set; }

        /// <summary>
        /// 审批耗时（秒）
        /// </summary>
        public long DurationSeconds { get; set; }

        /// <summary>
        /// 审批结果
        /// 枚举值：approved（通过）/ rejected_terminate（驳回终止）/ rejected_return（驳回回退）
        /// </summary>
        public string Outcome      { get; set; }

        /// <summary>
        /// 驳回原因（Outcome 为 rejected_* 时有值）
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// 审批意见
        /// </summary>
        public string Comment      { get; set; }

        /// <summary>
        /// 第几轮（驳回回退后重新审批时递增）
        /// </summary>
        public int Round           { get; set; }
    }

    /// <summary>
    /// 驳回轨迹渲染 DTO
    /// 对应 Flowgraph.vue 底部"驳回轨迹"列表展示
    /// </summary>
    public class RejectHistoryRenderDto
    {
        public string RejectId       { get; set; }
        public string RejectBy       { get; set; }
        public string RejectNodeId   { get; set; }
        public string RejectNodeName { get; set; }
        public string TargetNodeId   { get; set; }
        public string TargetNodeName { get; set; }
        public string RejectReason   { get; set; }
        public DateTime RejectTime   { get; set; }
    }
}
