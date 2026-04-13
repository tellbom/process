using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FlowableWrapper.Application.Dtos
{

    /// <summary>
    /// 流程列表条目（轻量，不含当前节点和审批历史）
    /// </summary>
    public class ProcessListItemDto
    {
        public string ProcessInstanceId    { get; set; }
        public string BusinessId           { get; set; }
        public string BusinessType         { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string Status               { get; set; }
        public string CreatedBy            { get; set; }
        public DateTime CreatedTime        { get; set; }
        public DateTime? CompletedTime     { get; set; }
    }

    /// <summary>
    /// 流程列表分页响应
    /// </summary>
    public class ProcessListResult
    {
        public List<ProcessListItemDto> Items { get; set; }
            = new List<ProcessListItemDto>();
        public int Total     { get; set; }
        public int PageIndex { get; set; }
        public int PageSize  { get; set; }
    }

    /// <summary>
    /// 审批历史查询请求
    /// </summary>
    public class AuditHistoryRequest
    {
        /// <summary>
        /// 业务 ID（必填）
        /// </summary>
        [Required(ErrorMessage = "businessId 不能为空")]
        public string BusinessId { get; set; }
    }
}
