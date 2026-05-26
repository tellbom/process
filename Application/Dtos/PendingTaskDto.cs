using FlowableWrapper.Domain.ElasticSearch;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FlowableWrapper.Application.Dtos
{
    // ════════════════════════════════════════════════════════════════
    // Phase 10 修正：替换 Phase 4 的 PendingTaskDto.cs
    //
    // 修正说明：
    //   保留原始 PageCode，同时新增 PageUrl：
    //     - PageCode 是 BPMN/slotConfig 中配置的原始页面标识或业务页面 URL
    //     - PageUrl 是流程中心为 iframe 场景拼好业务上下文后的 URL
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询待办任务请求
    /// </summary>
    public class GetPendingTasksRequest
    {
        [Required(ErrorMessage = "employeeId 不能为空")]
        public string EmployeeId   { get; set; }
        public string? BusinessType { get; set; }
        public int PageIndex       { get; set; } = 1;
        public int PageSize        { get; set; } = 20;
    }

    /// <summary>
    /// 待办任务 DTO（修正版）
    ///
    /// 设计原则：
    ///   流程中心告诉前端当前节点、原始页面配置以及可直接 iframe 渲染的 pageUrl。
    ///   对全自动流程，pageCode 可以是业务系统 URL；流程中心会把 businessId 等上下文拼到 pageUrl。
    ///
    /// 不包含：
    ///   ❌ requiredSlots（流程中心不推断业务系统的表单需要什么）
    ///   ❌ jumpUrl / jumpType（由 pageUrl 替代）
    ///   ❌ nextViewComponentPath（由业务表单组件自行处理）
    /// </summary>
    public class PendingTaskDto
    {
        /// <summary>
        /// Flowable Task ID
        /// 并行场景 complete 时传入，用于精确指定完成哪个任务
        /// 前端从此处取值，complete 请求时放入 CompleteTaskRequest.taskId
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// 任务名称（来自 BPMN userTask name 属性）
        /// </summary>
        public string TaskName { get; set; }

        /// <summary>
        /// 业务 ID
        /// </summary>
        public string BusinessId { get; set; }

        /// <summary>
        /// 业务类型
        /// </summary>
        public string BusinessType { get; set; }

        /// <summary>
        /// 节点业务语义
        /// 示例：GROUP_LEADER_CONFIRM / INSPECTION_OFFICE_REVIEW
        /// 前端可用于：标题展示、权限判断、特殊逻辑分支
        /// </summary>
        public string NodeSemantic { get; set; }

        /// <summary>
        /// Current node role key. Recommendations are keyed by this value.
        /// </summary>
        public string RoleKey { get; set; }

        /// <summary>
        /// 原始页面配置。
        /// 对组件化流程：可为组件编码，例如 SelectionApproval/IntegrityHeadHandleForm。
        /// 对 iframe 流程：可为业务系统 URL，例如 https://biz-system/form/approval。
        /// </summary>
        public string PageCode { get; set; }

        /// <summary>
        /// iframe 可直接使用的页面 URL。
        /// 当 PageCode 是 http/https URL 时，流程中心会追加 businessId、taskId、businessType、nodeId 等上下文参数。
        /// 当 PageCode 不是 URL 时，此字段为 null，前端继续按组件编码方式处理 PageCode。
        /// </summary>
        public string PageUrl { get; set; }

        /// <summary>
        /// 是否已过不可撤回节点
        /// true：流程已过收敛点（IsConvergencePoint=true 的节点），不允许撤回
        /// </summary>
        public bool IsAfterConvergencePoint { get; set; }

        /// <summary>
        /// 任务创建时间
        /// </summary>
        public DateTime CreateTime { get; set; }

        /// <summary>
        /// 任务优先级（来自 Flowable 任务优先级）
        /// </summary>
        public int Priority { get; set; }

        // 前端可以忽略此字段
        public List<SlotDefinition> RequiredSlots { get; set; }

        
        /// <summary>
        /// 当前节点是否可以被驳回
        /// </summary>
        public bool CanReject { get; set; }


        /// <summary>
        /// 当前节点驳回配置
        /// </summary>
        public List<RejectOption> RejectOptions { get; set; }

        /// <summary>
        /// Current node assignee recommendations. Key is roleKey, value is recommended user ids.
        /// </summary>
        public Dictionary<string, List<string>> RecommendedUsers { get; set; }
            = new Dictionary<string, List<string>>();

        /// <summary>
        /// Selection restriction policy for downstream slots. Key is slotKey.
        /// </summary>
        public Dictionary<string, bool> RestrictToRecommended { get; set; }
            = new Dictionary<string, bool>();
    }

    /// <summary>
    /// 待办任务分页响应
    /// </summary>
    public class PendingTaskPageResult
    {
        public List<PendingTaskDto> Items { get; set; } = new();
        public int Total     { get; set; }
        public int PageIndex { get; set; }
        public int PageSize  { get; set; }
    }
}
