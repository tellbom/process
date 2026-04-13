using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlowableWrapper.Domain.Flowable
{
    /// <summary>
    /// Flowable Runtime Service 接口
    /// 对应 Flowable REST API 的 /runtime/ 路径
    /// </summary>
    public interface IFlowableRuntimeService
    {
        Task<FlowableProcessInstance> StartProcessInstanceByKeyAsync(
            string processDefinitionKey,
            string businessKey,
            Dictionary<string, object> variables);

        Task<FlowableProcessInstance> GetProcessInstanceAsync(string processInstanceId);

        Task DeleteProcessInstanceAsync(string processInstanceId, string deleteReason);

        /// <summary>
        /// 强制迁移流程节点（驳回跳转使用）
        /// 取消所有指定活动节点，跳转到目标节点
        /// 并行场景：cancelActivityIds 传入所有并行分支 taskDefinitionKey
        /// 单节点场景：cancelActivityIds 只传一个
        /// </summary>
        Task ChangeActivityStateAsync(
            string processInstanceId,
            List<string> cancelActivityIds,
            string startActivityId);
    }
}