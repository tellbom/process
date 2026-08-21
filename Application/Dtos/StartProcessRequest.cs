using FlowableWrapper.Application.Slots;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// 启动流程请求
    /// </summary>
    public class StartProcessRequest
    {
        [Required(ErrorMessage = "businessType 不能为空")]
        public string BusinessType { get; set; }

        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }

        /// <summary>
        /// 业务单据标题，用于待办列表直接展示。调用方未传时返回空值，
        /// 流程中心不会从 businessVariables 猜测标题字段。
        /// </summary>
        public string? BusinessTitle { get; set; }

        /// <summary>
        /// Stable caller request identifier. Network retries must reuse it.
        /// When omitted, businessId is used as the request identity.
        /// </summary>
        public string RequestId { get; set; }

        /// <summary>
        /// 首节点选人（基于 Slot 契约）
        /// 传空数组时通过 businessVariables 直接传 assignee 变量名也可
        /// 推荐人通过 AssigneeContract 传入（roleKey 维度），不通过此字段。
        /// </summary>
        public List<SlotSelection> InitialSlotSelections { get; set; }
            = new List<SlotSelection>();

        /// <summary>
        /// 业务变量（直接注入 Flowable 启动变量）
        /// 用途：assignee 变量（如 deptHeadAssignee）、网关条件变量等
        /// starterAssignee 由流程中心从当前登录用户自动注入，无需传入
        /// </summary>
        public Dictionary<string, object> BusinessVariables { get; set; }
            = new Dictionary<string, object>();

        /// <summary>流程结束后回调业务系统的配置</summary>
        public CallbackConfigDto Callback { get; set; }

        /// <summary>
        /// 按业务角色 Key 传入推荐处理人，内部展开为 RecommendedAssigneesSnapshot。
        /// 不注入任何 Flowable 变量，不影响执行路径；最终生效人员仍来自 NextSlotSelections。
        /// roleKey 对应 slot.json 中各节点配置的 roleKey 字段。
        /// </summary>
        public AssigneeContract? AssigneeContract { get; set; }
    }

    public class AssigneeContract
    {
        public List<RoleAssignment> Roles { get; set; } = new List<RoleAssignment>();

        /// <summary>
        /// 本次流程实例中各节点的动态详细说明。
        /// 以数组承载，避免 roleKey 成为 Elasticsearch 动态字段。
        /// 此数据只作为流程实例业务快照，不参与 Flowable 选人和路由。
        /// </summary>
        public List<NodeDescriptionInput> NodeDescriptions { get; set; }
            = new List<NodeDescriptionInput>();
    }

    public class NodeDescriptionInput
    {
        /// <summary>与 NodeSemanticInfo.RoleKey 对应的节点角色 Key。</summary>
        public string RoleKey { get; set; }

        /// <summary>发起人针对本次流程填写的节点详细说明。</summary>
        public string Description { get; set; }
    }

    public class RoleAssignment
    {
        /// <summary>Business role key matching NodeSemanticInfo.RoleKey.</summary>
        public string RoleKey { get; set; }

        /// <summary>single / multiple，仅作调用方语义描述，不参与 Flowable 变量投影。</summary>
        public string Mode { get; set; }

        public List<string> Users { get; set; } = new List<string>();
    }

    public class CallbackConfigDto
    {
        public string Url { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public int RetryCount { get; set; } = 3;
        public Dictionary<string, string> Headers { get; set; }
            = new Dictionary<string, string>();
    }

    public class StartProcessResponse
    {
        public string ProcessInstanceId { get; set; }
        public string BusinessId { get; set; }
        public string FirstTaskId { get; set; }
        public string FirstNodeSemantic { get; set; }
        public string FirstPageCode { get; set; }
    }

    /// <summary>
    /// 流程列表查询请求
    /// </summary>
    public class ProcessListRequest
    {
        public string? BusinessId { get; set; }
        public string? BusinessType { get; set; }
        public string? Status { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? CreatedTimeFrom { get; set; }
        public DateTime? CreatedTimeTo { get; set; }
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
