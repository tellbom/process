using System.Threading.Tasks;

namespace FlowableWrapper.Domain.Flowable
{
    /// <summary>
    /// Flowable Repository Service 接口
    /// 对应 Flowable REST API 的 /repository/ 路径
    /// 用于 BPMN 部署和流程定义查询
    /// </summary>
    public interface IFlowableRepositoryService
    {
        /// <summary>
        /// 部署 BPMN 文件
        /// </summary>
        Task<FlowableDeployment> DeployBpmnAsync(string fileName, string bpmnXml);

        /// <summary>
        /// 按 processDefinitionKey 查询最新版本的流程定义
        /// </summary>
        Task<FlowableProcessDefinition> GetLatestProcessDefinitionByKeyAsync(
            string processDefinitionKey);

        /// <summary>
        /// 删除部署（cascade=true 同时删除流程实例）
        /// </summary>
        Task DeleteDeploymentAsync(string deploymentId, bool cascade);


        /// <summary>
        /// 获取流程定义的 BPMN XML 内容
        /// 对应 Flowable REST: GET /repository/process-definitions/{id}/resourcedata
        /// </summary>
        Task<string> GetBpmnXmlByDefinitionIdAsync(string processDefinitionId);
    }


}
