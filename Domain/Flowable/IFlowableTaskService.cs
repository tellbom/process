using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlowableWrapper.Domain.Flowable
{
    /// <summary>
    /// Flowable Task Service 接口
    /// 对应 Flowable REST API 的 /runtime/tasks/ 路径
    /// </summary>
    public interface IFlowableTaskService
    {
        /// <summary>
        /// 按 taskId 查询单个任务
        /// </summary>
        Task<FlowableTask> GetTaskAsync(string taskId);

        /// <summary>
        /// 按条件查询任务列表
        /// </summary>
        Task<List<FlowableTask>> QueryTasksAsync(FlowableTaskQuery query);

        /// <summary>
        /// 完成任务，传入流程变量（变量驱动下一节点 assignee）
        /// </summary>
        Task CompleteAsync(string taskId, Dictionary<string, object> variables);

        /// <summary>
        /// 认领任务（将 candidateUser 转为 assignee）
        /// </summary>
        Task ClaimAsync(string taskId, string userId);

        /// <summary>
        /// 设置单一处理人
        /// </summary>
        Task SetAssigneeAsync(string taskId, string userId);

        /// <summary>
        /// 批量添加候选人（用于多人候选场景）
        /// </summary>
        Task AddCandidateUsersAsync(string taskId, List<string> userIds);

        /// <summary>
        /// 查询任务的候选人列表
        /// </summary>
        Task<List<string>> GetCandidateUsersAsync(string taskId);

        /// <summary>
        /// 清空候选人列表
        /// </summary>
        Task ClearCandidateUsersAsync(string taskId);
    }
}
