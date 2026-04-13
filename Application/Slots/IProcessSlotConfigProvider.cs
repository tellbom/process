using FlowableWrapper.Domain.ElasticSearch;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlowableWrapper.Application.Slots
{
    /// <summary>
    /// 流程节点 Slot 配置读取接口
    /// 从 ES 的 ProcessDefinitionSemanticDocument 中读取指定节点的 Slot 定义
    /// </summary>
    public interface IProcessSlotConfigProvider
    {
        /// <summary>
        /// 获取指定流程定义 + 节点的 Slot 定义列表
        /// 若该节点无 Slot 定义则返回空列表（不抛异常）
        /// </summary>
        /// <param name="processDefinitionKey">流程定义 Key，如 personnel_selection_approval</param>
        /// <param name="taskDefinitionKey">节点 Key，如 ut01_group_leader_confirm</param>
        Task<List<SlotDefinition>> GetSlotsForNodeAsync(
            string processDefinitionKey,
            string taskDefinitionKey);

        /// <summary>
        /// 获取指定流程定义所有节点的语义信息
        /// 用于查待办任务时批量补充 nodeSemantic / pageCode / requiredSlots
        /// </summary>
        /// <param name="processDefinitionKey">流程定义 Key</param>
        Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(
            string processDefinitionKey);
    }
}
