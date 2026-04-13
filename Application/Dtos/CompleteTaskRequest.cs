using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FlowableWrapper.Application.Slots;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// 完成（审批）任务请求（修正版）
    ///
    /// 调用方说明：
    ///   1. action = Approve 时，框架将 isApproved=true 注入流程变量
    ///   2. action = Reject 时，rejectReason 必填，框架注入 isApproved=false
    ///      BPMN 排他网关走 false 分支到 endEvent，Flowable 自然结束，框架不手动删除流程实例
    ///   3. nextSlotSelections 在当前任务 complete 时一并传入
    ///      框架通过 SlotVariableConverter 转换为 Flowable 变量
    ///      Flowable 自动将变量应用到下一节点的 assignee/collection
    ///   4. taskId 在以下场景必须传入：
    ///      - 同一用户同时是多个并行节点的处理人（不传会随机取一个）
    ///      普通单节点 / 会签场景不需要传，框架自动定位
    /// </summary>
    public class CompleteTaskRequest
    {
        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }

        /// <summary>
        /// 指定任务 ID（可选）
        /// 并行场景同一用户有多个任务时必填，从待办列表的 taskId 字段取值
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>操作人工号，不传则读 Header X-User-Id</summary>
        public string EmployeeId { get; set; }

        /// <summary>1=通过，2=驳回</summary>
        [Required(ErrorMessage = "action 不能为空")]
        public ApprovalAction Action { get; set; }

        /// <summary>
        /// 驳回模式代码（Action=Reject 时必填）
        /// 必须是当前节点 CanReject=true 且 RejectOptions 中存在的 rejectCode
        /// 示例：TO_STARTER / TO_DEPT_HEAD / TO_DEPT_APPROVE
        /// </summary>
        public string RejectCode { get; set; }

        /// <summary>驳回原因（Action=Reject 时必填）</summary>
        public string RejectReason { get; set; }

        /// <summary>审批意见（可选，写入审计记录）</summary>
        public string Comment { get; set; }

        /// <summary>
        /// 下一节点选人（Action=Approve 且下一节点有 Slot 时填写）
        /// 框架通过 SlotVariableConverter 转换为 Flowable 变量
        /// </summary>
        public List<SlotSelection> NextSlotSelections { get; set; }
            = new List<SlotSelection>();

        /// <summary>
        /// 业务变量（注入 Flowable，用于网关条件判断）
        /// 例：{ "needFeedback": true }
        /// </summary>
        public Dictionary<string, object> BusinessVariables { get; set; }
            = new Dictionary<string, object>();
    }
    /// <summary>
    /// 完成任务响应（修正版）
    ///
    /// ⚠ 移除了 ProcessEnded 字段
    /// 流程是否结束由 Flowable 自己走到 endEvent 后触发 HTTP Task 回调通知
    /// 框架在 ProcessCallbackAppService 中处理，更新 ES status = completed
    /// 前端查询流程状态请调用 GET /api/processes/{businessId}/progress
    /// </summary>
    public class CompleteTaskResponse
    {
        /// <summary>
        /// 操作是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 提示信息
        /// </summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 转派任务请求
    /// </summary>
    public class ReassignTaskRequest
    {
        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }

        /// <summary>
        /// 指定转派哪个任务（可选）
        /// 并行场景下必传，单节点场景不传时转派该流程下所有待办任务
        /// </summary>
        public string TaskId { get; set; }

        [Required(ErrorMessage = "newAssignees 不能为空")]
        public List<string> NewAssignees { get; set; } = new List<string>();

        /// <summary>
        /// 转派原因（可选，写入日志）
        /// </summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// 终止流程请求
    /// </summary>
    public class TerminateProcessRequest
    {
        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }

        [Required(ErrorMessage = "reason 不能为空")]
        public string Reason { get; set; }
    }
}
