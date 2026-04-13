using System.Collections.Generic;
using System.Threading.Tasks;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Infrastructure.Slots
{
    /// <summary>
    /// 从 ES 读取 Slot 配置的实现
    /// 数据来源：ProcessDefinitionSemanticDocument.NodeSemanticMap
    /// 该文档在部署 BPMN 时由 BpmnDeploymentAppService 写入（Phase 7）
    /// </summary>
    public class ElasticSearchSlotConfigProvider : IProcessSlotConfigProvider
    {
        private readonly IElasticSearchService _esService;
        private readonly ILogger<ElasticSearchSlotConfigProvider> _logger;

        public ElasticSearchSlotConfigProvider(
            IElasticSearchService esService,
            ILogger<ElasticSearchSlotConfigProvider> logger)
        {
            _esService = esService;
            _logger = logger;
        }

        public async Task<List<SlotDefinition>> GetSlotsForNodeAsync(
            string processDefinitionKey,
            string taskDefinitionKey)
        {
            var map = await _esService.GetNodeSemanticMapAsync(processDefinitionKey);

            if (map == null || !map.TryGetValue(taskDefinitionKey, out var nodeInfo))
            {
                _logger.LogWarning(
                    "未找到节点语义配置: ProcessDefinitionKey={ProcessDefinitionKey}, " +
                    "TaskDefinitionKey={TaskDefinitionKey}",
                    processDefinitionKey, taskDefinitionKey);
                return new List<SlotDefinition>();
            }

            return nodeInfo.Slots ?? new List<SlotDefinition>();
        }

        public async Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(
            string processDefinitionKey)
        {
            return await _esService.GetNodeSemanticMapAsync(processDefinitionKey);
        }
    }
}
