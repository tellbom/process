using System.Text.Json;
using System.Text.Json.Nodes;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;

namespace FlowableWrapper.Application.Services;

public sealed class WorkflowWorkbenchAppService
{
    private readonly IFlowableRepositoryService _repository;
    private readonly IWorkflowReliabilityStore _store;
    private readonly IElasticSearchService _es;
    public WorkflowWorkbenchAppService(IFlowableRepositoryService repository, IWorkflowReliabilityStore store, IElasticSearchService es)
        => (_repository, _store, _es) = (repository, store, es);

    public async Task<WorkflowDefinitionPage> GetDefinitionsAsync(string? keyword, int pageIndex, int pageSize)
    {
        if (pageIndex < 1 || pageSize < 1 || pageSize > 100 || (long)(pageIndex - 1) * pageSize > int.MaxValue)
            throw new BusinessException("分页参数无效。", "INVALID_PAGINATION");
        var page = await _repository.QueryLatestProcessDefinitionsAsync(keyword, (pageIndex - 1) * pageSize, pageSize);
        return new() { Items = page.Items, Total = page.Total };
    }

    public async Task<WorkflowDefinitionArtifact> GetArtifactAsync(string key, int? version = null)
    {
        if (version.HasValue && version.Value < 1)
            throw new BusinessException("流程定义版本无效。", "INVALID_DEFINITION_VERSION");
        var definition = version.HasValue
            ? await _repository.GetProcessDefinitionByKeyAndVersionAsync(key, version.Value)
            : await _repository.GetLatestProcessDefinitionByKeyAsync(key);
        if (definition == null) throw new BusinessException("Flowable 中没有此流程定义。", "PROCESS_DEFINITION_NOT_FOUND");
        var xml = await _repository.GetBpmnXmlByDefinitionIdAsync(definition.Id);
        if (string.IsNullOrWhiteSpace(xml)) throw new BusinessException("Flowable 中的 BPMN 资源不可读取。", "BPMN_RESOURCE_NOT_FOUND");
        var embedded = WorkflowArtifactCompiler.ReadEmbeddedConfiguration(WorkflowArtifactCompiler.Parse(xml));
        if (embedded != null) return new() { Definition = definition, BpmnXml = xml, SlotConfigJson = embedded };

        // 老部署没有携带 sidecar，显式标记兼容来源，重新发布后由 Flowable 资源完整承载。
        var stored = await _store.GetDefinitionConfigAsync(key, definition.Version);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var map = stored != null && stored.ProcessDefinitionId == definition.Id
            ? JsonSerializer.Deserialize<Dictionary<string, NodeSemanticInfo>>(stored.ConfigJson, options) ?? new()
            : await _es.GetNodeSemanticMapAsync(key);
        var configs = new JsonArray();
        foreach (var node in map.Values)
        {
            var value = JsonSerializer.SerializeToNode(node, options)!.AsObject();
            value.Remove("callbackUrlSpecified");
            if (!node.CallbackUrlSpecified) value.Remove("callbackUrl");
            configs.Add(value);
        }
        return new() { Definition = definition, BpmnXml = xml, SlotConfigJson = configs.ToJsonString(options), ConfigurationSource = "legacy-projection" };
    }

    // 与正式启动接口使用相同的最新定义及不可变配置记录，只读取，不激活或创建实例。
    public async Task<WorkflowDefinitionReadiness> GetReadinessAsync(string key)
    {
        var definition = await _repository.GetLatestProcessDefinitionByKeyAsync(key);
        var result = new WorkflowDefinitionReadiness { Definition = definition };
        result.Checks.Add(new()
        {
            Code = "FLOWABLE_DEFINITION", Label = "流程已部署", Passed = definition != null,
            Detail = definition == null ? "Flowable 中尚未找到此流程，请先校验并发布。" : $"已读取版本 {definition.Version}。",
            Action = definition == null ? "publish" : ""
        });
        if (definition == null) return result;
        var config = await _store.GetDefinitionConfigAsync(key, definition.Version);
        var matches = config != null && string.Equals(config.ProcessDefinitionId, definition.Id, StringComparison.Ordinal);
        result.Checks.Add(new()
        {
            Code = "ACTIVE_CONFIGURATION", Label = "发布配置已登记", Passed = config != null,
            Detail = config == null ? $"版本 {definition.Version} 未登记到流程中心。历史文件可编辑，但需要通过正式发布完成 ACTIVE 门禁。" : "已找到当前版本的不可变发布配置。",
            Action = config == null ? "publish" : ""
        });
        result.Checks.Add(new()
        {
            Code = "ACTIVE_DEFINITION_MATCH", Label = "登记与当前版本一致", Passed = matches,
            Detail = matches ? "配置绑定的定义与 Flowable 当前版本一致。" : config == null ? "需先完成发布登记，再核对定义绑定。" : "发布配置绑定了其他定义，不能用它启动当前版本。请重新校验并正式发布。",
            Action = matches ? "" : "publish"
        });
        return result;
    }
}
