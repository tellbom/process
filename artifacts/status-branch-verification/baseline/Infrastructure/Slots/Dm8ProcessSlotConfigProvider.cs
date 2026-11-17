using System.Collections.Concurrent;
using System.Text.Json;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Slots;

/// <summary>
/// 运行实例带版本时从 DM8 读取不可变规则；无版本的历史调用显式回退 ES。
/// 回退仅用于存量兼容，不会把“最新 ES 配置”伪装成某个已知版本。
/// </summary>
public sealed class Dm8ProcessSlotConfigProvider : IProcessSlotConfigProvider
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<Dictionary<string, NodeSemanticInfo>>>>
        VersionedConfigCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkflowReliabilityStore _store;
    private readonly IElasticSearchService _esService;
    private readonly Dm8Options _options;
    private readonly ILogger<Dm8ProcessSlotConfigProvider> _logger;
    private readonly IFlowableRepositoryService? _repository;
    private readonly BpmnDeploymentAppService? _artifacts;

    public Dm8ProcessSlotConfigProvider(
        IWorkflowReliabilityStore store,
        IElasticSearchService esService,
        IOptions<Dm8Options> options,
        ILogger<Dm8ProcessSlotConfigProvider> logger,
        IFlowableRepositoryService? repository = null,
        BpmnDeploymentAppService? artifacts = null)
    {
        _store = store;
        _esService = esService;
        _options = options.Value;
        _logger = logger;
        _repository = repository;
        _artifacts = artifacts;
    }

    public async Task<List<SlotDefinition>> GetSlotsForNodeAsync(
        string processDefinitionKey,
        string taskDefinitionKey,
        int? processDefinitionVersion = null)
    {
        var map = await GetNodeSemanticMapAsync(
            processDefinitionKey, processDefinitionVersion);
        return map.TryGetValue(taskDefinitionKey, out var node)
            ? node.Slots ?? new List<SlotDefinition>()
            : new List<SlotDefinition>();
    }

    public async Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(
        string processDefinitionKey,
        int? processDefinitionVersion = null)
    {
        if (!processDefinitionVersion.HasValue && _repository != null)
        {
            var latest = await _repository.GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);
            if (latest == null) throw new InvalidOperationException($"Flowable definition not found: {processDefinitionKey}.");
            var xml = await _repository.GetBpmnXmlByDefinitionIdAsync(latest.Id);
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidOperationException($"Flowable BPMN resource missing: {latest.Id}.");
            if (WorkflowArtifactCompiler.ReadEmbeddedConfiguration(WorkflowArtifactCompiler.Parse(xml)) != null)
                return _artifacts!.ReadArtifactNodeMap(xml);
        }
        if (_options.Enabled && processDefinitionVersion.HasValue)
        {
            var cacheKey = $"{processDefinitionKey}:{processDefinitionVersion.Value}";
            var lazyConfig = VersionedConfigCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<Dictionary<string, NodeSemanticInfo>>>(
                    () => LoadVersionedConfigAsync(
                        processDefinitionKey,
                        processDefinitionVersion.Value),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lazyConfig.Value;
            }
            catch
            {
                VersionedConfigCache.TryRemove(
                    new KeyValuePair<string, Lazy<Task<Dictionary<string, NodeSemanticInfo>>>>(
                        cacheKey,
                        lazyConfig));
                throw;
            }
        }

        if (_options.Enabled)
        {
            _logger.LogWarning(
                "Definition version is missing; using legacy ES rules. DefinitionKey={DefinitionKey}",
                processDefinitionKey);
        }
        return await _esService.GetNodeSemanticMapAsync(processDefinitionKey);
    }

    private async Task<Dictionary<string, NodeSemanticInfo>> LoadVersionedConfigAsync(
        string processDefinitionKey,
        int processDefinitionVersion)
    {
        if (_repository != null && _artifacts != null)
        {
            var definition = await _repository.GetProcessDefinitionByKeyAndVersionAsync(processDefinitionKey, processDefinitionVersion);
            if (definition == null) throw new InvalidOperationException($"Flowable definition not found: {processDefinitionKey} v{processDefinitionVersion}.");
            var xml = await _repository.GetBpmnXmlByDefinitionIdAsync(definition.Id);
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidOperationException($"Flowable BPMN resource missing: {definition.Id}.");
            if (WorkflowArtifactCompiler.ReadEmbeddedConfiguration(WorkflowArtifactCompiler.Parse(xml)) != null)
                return _artifacts.ReadArtifactNodeMap(xml);
        }
        var config = await _store.GetDefinitionConfigAsync(
            processDefinitionKey,
            processDefinitionVersion);
        if (config == null)
            throw new InvalidOperationException(
                $"Versioned workflow config not found: {processDefinitionKey} v{processDefinitionVersion}.");

        return JsonSerializer.Deserialize<Dictionary<string, NodeSemanticInfo>>(
                   config.ConfigJson,
                   JsonOptions)
               ?? new Dictionary<string, NodeSemanticInfo>(
                   StringComparer.OrdinalIgnoreCase);
    }
}
