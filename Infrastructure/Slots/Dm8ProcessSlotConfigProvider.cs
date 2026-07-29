using System.Text.Json;
using FlowableWrapper.Application.Slots;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkflowReliabilityStore _store;
    private readonly IElasticSearchService _esService;
    private readonly Dm8Options _options;
    private readonly ILogger<Dm8ProcessSlotConfigProvider> _logger;

    public Dm8ProcessSlotConfigProvider(
        IWorkflowReliabilityStore store,
        IElasticSearchService esService,
        IOptions<Dm8Options> options,
        ILogger<Dm8ProcessSlotConfigProvider> logger)
    {
        _store = store;
        _esService = esService;
        _options = options.Value;
        _logger = logger;
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
        if (_options.Enabled && processDefinitionVersion.HasValue)
        {
            var config = await _store.GetDefinitionConfigAsync(
                processDefinitionKey, processDefinitionVersion.Value);
            if (config == null)
                throw new InvalidOperationException(
                    $"Versioned workflow config not found: {processDefinitionKey} v{processDefinitionVersion}.");
            return JsonSerializer.Deserialize<Dictionary<string, NodeSemanticInfo>>(
                       config.ConfigJson, JsonOptions)
                   ?? new Dictionary<string, NodeSemanticInfo>(
                       StringComparer.OrdinalIgnoreCase);
        }

        if (_options.Enabled)
        {
            _logger.LogWarning(
                "Definition version is missing; using legacy ES rules. DefinitionKey={DefinitionKey}",
                processDefinitionKey);
        }
        return await _esService.GetNodeSemanticMapAsync(processDefinitionKey);
    }
}
