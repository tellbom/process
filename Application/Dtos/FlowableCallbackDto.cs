using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FlowableWrapper.Application.Dtos
{
    /// <summary>
    /// Flowable HTTP Task 回调请求体
    /// Flowable 的 HTTP serviceTask 完成后，POST 到 frameworkCallbackUrl
    /// Controller 接收后交给 ProcessCallbackAppService 处理
    /// </summary>
    public class FlowableCallbackRequest
    {
        /// <summary>
        /// Flowable 流程实例 ID
        /// 由 BPMN 中 ${execution.processInstanceId} 注入
        /// </summary>
        [Required(ErrorMessage = "processInstanceId 不能为空")]
        public string ProcessInstanceId { get; set; }

        /// <summary>
        /// 业务 ID
        /// 由流程变量 ${businessId} 注入
        /// </summary>
        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }

        /// <summary>
        /// 流程定义 Key
        /// 由流程变量 ${processDefinitionKey} 注入
        /// </summary>
        public string ProcessDefinitionKey { get; set; }

        /// <summary>
        /// Flowable variables passed through by the HTTP task.
        /// </summary>
        public Dictionary<string, object> Variables { get; set; }
            = new Dictionary<string, object>();
    }

    /// <summary>
    /// 框架回调处理结果（返回给 Flowable HTTP Task）
    /// Flowable 根据 HTTP 状态码判断是否成功：
    ///   2xx → 成功，流程继续
    ///   非 2xx → 失败，触发重试，最终进死信队列
    /// </summary>
    public class FlowableCallbackResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Payload sent for a node-level on_complete callback.
    /// </summary>
    public class NodeCallbackPayload
    {
        public string BusinessId { get; set; }
        public string ProcessInstanceId { get; set; }
        public string TaskDefinitionKey { get; set; }
        public string CallbackTiming { get; set; }
        public DateTime TriggeredAt { get; set; }
    }
}
