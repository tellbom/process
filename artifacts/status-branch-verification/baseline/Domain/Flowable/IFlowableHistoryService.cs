using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlowableWrapper.Domain.Flowable
{
    /// <summary>
    /// Flowable History Service 接口
    /// 对应 Flowable REST API 的 /history/ 路径
    /// </summary>
    public interface IFlowableHistoryService
    {
        /// <summary>
        /// 查询历史任务（已完成的任务）
        /// </summary>
        Task<List<FlowableHistoricTask>> QueryHistoricTasksAsync(
            FlowableHistoricTaskQuery query);
    }
}
