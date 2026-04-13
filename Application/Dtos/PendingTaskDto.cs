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
    //   删除 RequiredSlots 字段
    //   原因：Slot 是流程中心内部的变量映射配置，不是前端渲染依据
    //         前端表单组件（通过 pageCode → COMPONENT_REGISTRY 找到）
    //         自身包含了选人逻辑，不依赖流程中心下发 Slot 定义
    //
    //   保留 NodeSemantic + PageCode：前端通过 pageCode 在 COMPONENT_REGISTRY
    //   中找到对应的 Vue 组件并渲染，这是正确的解耦方式
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
    ///   流程中心只告诉前端"你在哪个节点"（nodeSemantic + pageCode）
    ///   表单渲染、选人逻辑、下一步是否选人，全部由业务系统的表单组件自己决定
    ///   前端通过 pageCode 查 COMPONENT_REGISTRY 找到对应组件并挂载
    ///
    /// 不包含：
    ///   ❌ requiredSlots（流程中心不推断业务系统的表单需要什么）
    ///   ❌ jumpUrl / jumpType（由 pageCode 替代）
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
        /// 页面编码
        /// 示例：PersonnelSelection/InspectionGroupForm
        /// 前端通过 COMPONENT_REGISTRY[pageCode] 找到对应的 Vue 组件并渲染
        /// 流程中心不感知 Vue 路由，不返回路由地址
        /// </summary>
        public string PageCode { get; set; }

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
