using System;
using System.Collections.Generic;

namespace FlowableWrapper.Domain.Flowable
{
    // ═══════════════════════════════════════════════════════════════
    // 流程实例
    // ═══════════════════════════════════════════════════════════════

    public class FlowableProcessInstance
    {
        public string Id { get; set; }
        public string ProcessDefinitionId { get; set; }
        public string ProcessDefinitionKey { get; set; }
        public string BusinessKey { get; set; }
        public bool IsEnded { get; set; }
        public bool IsSuspended { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // 任务
    // ═══════════════════════════════════════════════════════════════

    public class FlowableTask
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProcessInstanceId { get; set; }
        public string ProcessDefinitionId { get; set; }
        /// <summary>
        /// 对应 BPMN 中的 userTask id 属性
        /// </summary>
        public string TaskDefinitionKey { get; set; }
        public string Assignee { get; set; }
        public string Owner { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime? DueDate { get; set; }
        public int Priority { get; set; }
    }

    public class FlowableTaskQuery
    {
        public string ProcessInstanceId { get; set; }
        public string Assignee { get; set; }
        public string CandidateUser { get; set; }
        /// <summary>
        /// Flowable 的 involvedUser 查询会在引擎侧合并办理人、候选人和参与人，
        /// 可避免客户端对 assignee/candidate 两个集合做不精确的深分页归并。
        /// </summary>
        public string InvolvedUser { get; set; }
        public int Start { get; set; }
        public int Size { get; set; } = 100;
        public string Sort { get; set; } = "createTime";
        public string Order { get; set; } = "desc";
        /// <summary>
        /// 批量查询多个流程实例下的任务
        /// </summary>
        public List<string> ProcessInstanceIds { get; set; }
    }

    public class FlowableTaskPage
    {
        public List<FlowableTask> Items { get; set; } = new();
        public int Total { get; set; }
        public int Start { get; set; }
        public int Size { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // 历史任务
    // ═══════════════════════════════════════════════════════════════

    public class FlowableHistoricTask
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProcessInstanceId { get; set; }
        public string TaskDefinitionKey { get; set; }
        public string Assignee { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long? DurationInMillis { get; set; }
        /// <summary>
        /// 任务完成时传入的删除原因（驳回时会有值）
        /// </summary>
        public string DeleteReason { get; set; }
    }

    public class FlowableHistoricTaskQuery
    {
        public string ProcessInstanceId { get; set; }
        public bool? Finished { get; set; }
        public string TaskDefinitionKey { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // 部署 & 流程定义
    // ═══════════════════════════════════════════════════════════════

    public class FlowableDeployment
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime DeploymentTime { get; set; }
    }

    public class FlowableProcessDefinition
    {
        public string Id { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public int Version { get; set; }
        public string DeploymentId { get; set; }
        public string ResourceName { get; set; }
    }
}
