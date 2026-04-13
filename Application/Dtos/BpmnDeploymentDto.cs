using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FlowableWrapper.Domain.ElasticSearch;
using Microsoft.AspNetCore.Http;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// BPMN 部署请求（multipart/form-data）
    /// </summary>
    public class BpmnDeployRequest
    {
        [Required]
        public IFormFile File { get; set; }

        /// <summary>
        /// Slot + 节点语义配置 JSON 字符串
        /// 格式见 NodeSlotConfig
        /// </summary>
        public string SlotConfigJson { get; set; }
    }

    /// <summary>
    /// 单个节点的完整配置（随 BPMN 部署提交）
    ///
    /// 包含：
    ///   - 节点语义（nodeSemantic / pageCode）
    ///   - 驳回能力（canReject / rejectOptions）
    ///   - 驳回目标（isRejectTarget / rejectCode）
    ///   - 选人契约（slots）
    /// </summary>
    /// <summary>
    /// 单个节点的完整部署配置（随 BPMN 文件一起提交）
    /// </summary>
    public class NodeSlotConfig
    {
        /// <summary>必须与 BPMN userTask id 一致</summary>
        [Required]
        public string TaskDefinitionKey { get; set; }

        // ── 节点语义（可覆盖 BPMN extensionElements 中的值）────────
        public string NodeSemantic { get; set; }
        public string PageCode { get; set; }

        // ── 节点元数据 ───────────────────────────────────────────────
        /// <summary>是否为发起人节点，一个流程只能有一个</summary>
        public bool IsStarterNode { get; set; }
        /// <summary>是否不可撤回节点</summary>
        public bool IsConvergencePoint { get; set; }

        // ── 驳回能力（源节点维度）────────────────────────────────────
        /// <summary>当前节点是否具备驳回能力，true = 前端显示驳回按钮</summary>
        public bool CanReject { get; set; }
        /// <summary>CanReject=true 时必填，至少一个选项</summary>
        public List<RejectOptionConfig> RejectOptions { get; set; } = new();

        // ── 驳回目标（目标节点维度）──────────────────────────────────
        /// <summary>当前节点是否可作为驳回目标</summary>
        public bool IsRejectTarget { get; set; }
        /// <summary>驳回目标代码，IsRejectTarget=true 时必填，流程内全局唯一</summary>
        public string RejectCode { get; set; }

        // ── 选人契约 ─────────────────────────────────────────────────
        public List<SlotDefinition> Slots { get; set; } = new();
    }

    /// <summary>
    /// 驳回选项配置（部署时提交）
    /// </summary>
    public class RejectOptionConfig
    {
        [Required] public string RejectCode { get; set; }
        [Required] public string Label { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// BPMN 部署响应
    /// </summary>
    public class BpmnDeploymentResponse
    {
        public string DeploymentId { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string ProcessDefinitionName { get; set; }
        public int Version { get; set; }
        public DateTime DeploymentTime { get; set; }
        public int NodeSemanticCount { get; set; }
        public List<NodeSemanticSummary> Nodes { get; set; } = new();
    }
    /// <summary>
    /// 部署响应中的节点语义摘要（便于调用方确认部署结果）
    /// </summary>
    public class NodeSemanticSummary
    {
        public string TaskDefinitionKey { get; set; }
        public string NodeSemantic { get; set; }
        public string PageCode { get; set; }
        public bool IsStarterNode { get; set; }
        public bool IsConvergencePoint { get; set; }
        public bool CanReject { get; set; }
        public bool IsRejectTarget { get; set; }
        public string RejectCode { get; set; }
        public int SlotCount { get; set; }
        public int RejectOptionCount { get; set; }
    }


    /// <summary>
    /// GET /api/flowable/bpmn/{processDefinitionKey}/nodes 响应
    /// </summary>
    public class ProcessDefinitionNodeDto
    {
        public string TaskDefinitionKey { get; set; }
        public string NodeSemantic { get; set; }
        public string PageCode { get; set; }
        public bool IsStarterNode { get; set; }
        public bool IsConvergencePoint { get; set; }
        public bool CanReject { get; set; }
        public List<RejectOption> RejectOptions { get; set; } = new();
        public bool IsRejectTarget { get; set; }
        public string RejectCode { get; set; }
        public List<SlotDefinition> Slots { get; set; } = new();
    }
}
