using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Dm8;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FlowableWrapper.Infrastructure.Readiness;

public sealed class ReadinessWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly IElasticSearchService _elasticSearch;
    private readonly CallbackWorkerTelemetry _workerTelemetry;
    private readonly ApplicationReadinessState _state;
    private readonly ReadinessOptions _options;
    private readonly Dm8Options _dm8Options;
    private readonly CallbackWorkerOptions _workerOptions;
    private readonly ILogger<ReadinessWarmupService> _logger;
    private bool _indexesInitialized;

    public ReadinessWarmupService(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        IElasticSearchService elasticSearch,
        CallbackWorkerTelemetry workerTelemetry,
        ApplicationReadinessState state,
        IOptions<ReadinessOptions> options,
        IOptions<Dm8Options> dm8Options,
        IOptions<CallbackWorkerOptions> workerOptions,
        ILogger<ReadinessWarmupService> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _elasticSearch = elasticSearch;
        _workerTelemetry = workerTelemetry;
        _state = state;
        _options = options.Value;
        _dm8Options = dm8Options.Value;
        _workerOptions = workerOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _state.SetReady(new Dictionary<string, string>
            {
                ["readiness"] = "disabled"
            });
            _logger.LogWarning(
                "Readiness dependency gate is disabled. This setting is not suitable for production.");
            return;
        }

        var hasEverBeenReady = false;
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var dependencies = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                await ProbeRedisAsync(stoppingToken);
                dependencies["redis"] = "ready";

                if (!_dm8Options.Enabled)
                    throw new InvalidOperationException(
                        "DM8 reliability storage is disabled.");
                using (var scope = _scopeFactory.CreateScope())
                {
                    var store = scope.ServiceProvider
                        .GetRequiredService<IWorkflowReliabilityStore>();
                    await store.GetCallbackQueueSnapshotAsync(stoppingToken);
                    dependencies["dm8"] = "ready";

                    var repository = scope.ServiceProvider
                        .GetRequiredService<IFlowableRepositoryService>();
                    await repository.GetLatestProcessDefinitionByKeyAsync(
                        "__readiness_probe__");
                    dependencies["flowable"] = "ready";
                }

                if (_options.RequireElasticSearch)
                {
                    if (!_indexesInitialized)
                    {
                        await _elasticSearch.InitializeIndexesAsync();
                        _indexesInitialized = true;
                    }
                    else
                    {
                        await _elasticSearch.GetProcessMetadataBatchAsync(
                            new List<string>
                            {
                                "__readiness_probe__"
                            });
                    }
                    dependencies["elasticsearch"] = "ready";
                }
                else
                {
                    dependencies["elasticsearch"] = "degraded_allowed";
                }

                if (!_workerOptions.Enabled
                    || !_workerTelemetry.IsInitialized)
                {
                    throw new InvalidOperationException(
                        "Callback worker is not initialized.");
                }
                dependencies["callbackWorker"] = "ready";

                _state.SetReady(dependencies);
                hasEverBeenReady = true;
                consecutiveFailures = 0;
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(
                        _options.ProbeIntervalSeconds,
                        5,
                        300)),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                dependencies["failure"] = exception.Message;
                dependencies["consecutiveFailures"] =
                    consecutiveFailures.ToString();
                var failureThreshold = Math.Clamp(
                    _options.ConsecutiveFailureThreshold,
                    1,
                    10);
                if (!hasEverBeenReady
                    || consecutiveFailures >= failureThreshold)
                {
                    _state.SetNotReady(dependencies);
                }
                else
                {
                    dependencies["readiness"] =
                        "ready_with_transient_probe_failure";
                    _state.SetReady(dependencies);
                }
                _logger.LogWarning(
                    exception,
                    "Readiness probe failed. ConsecutiveFailures={ConsecutiveFailures}, FailureThreshold={FailureThreshold}, TrafficAccepted={TrafficAccepted}, Dependencies={Dependencies}",
                    consecutiveFailures,
                    failureThreshold,
                    hasEverBeenReady
                    && consecutiveFailures < failureThreshold,
                    dependencies);
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(
                        hasEverBeenReady
                            ? _options.ProbeIntervalSeconds
                            : _options.RetryIntervalSeconds,
                        hasEverBeenReady ? 5 : 1,
                        hasEverBeenReady ? 300 : 30)),
                    stoppingToken);
            }
        }
    }

    private async Task ProbeRedisAsync(CancellationToken cancellationToken)
    {
        var database = _redis.GetDatabase();
        var key = $"process-center:readiness:{Environment.MachineName}:{Environment.ProcessId}";
        var value = Guid.NewGuid().ToString("N");
        if (!await database.StringSetAsync(
                key,
                value,
                TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException(
                "Redis readiness write returned false.");
        var loaded = await database.StringGetAsync(key);
        if (!string.Equals(loaded, value, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Redis readiness read did not return the written value.");
        await database.KeyDeleteAsync(key);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
