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
        /// 首节点选人（基于 Slot 契约）
        /// 传空数组时通过 businessVariables 直接传 assignee 变量名也可
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
        public string BusinessId { get; set; }
        public string BusinessType { get; set; }
        public string Status { get; set; }
        public string CreatedBy { get; set; }
        public DateTime? CreatedTimeFrom { get; set; }
        public DateTime? CreatedTimeTo { get; set; }
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}