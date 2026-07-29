using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using process.Domain.DistributedLock;

namespace FlowableWrapper.Application.Services
{
    /// <summary>
    /// 流程生命周期服务
    ///
    /// 职责（严格边界）：
    ///   ✔ 启动流程
    ///   ✔ 终止流程
    ///
    /// 核心设计约束：
    ///   原则3 — InitialSlotSelections 转换为变量后随 StartProcess 一次性传入
    ///            Flowable 启动时自动 assign 首节点任务，框架不补 SetAssignee
    ///   原则2 — 无 complete 后补 assignee、无轮询、无 PendingNextNode
    ///
    /// 最终一致性说明：
    ///   先调 Flowable 拿到 processInstanceId，再写 ES
    ///   若 ES 写入失败：记录错误日志 + 抛异常，流程已在 Flowable 中运行
    ///   这是已知的最终一致性窗口，通过监控告警兜底，不尝试回滚 Flowable
    /// </summary>
    public class ProcessLifecycleAppService
    {
        private readonly IFlowableRuntimeService _runtimeService;
        private readonly IFlowableTaskService _taskService;
        private readonly IFlowableRepositoryService _repositoryService;
        private readonly IElasticSearchService _esService;
        private readonly IProcessSlotConfigProvider _slotConfigProvider;
        private readonly SlotVariableConverter _slotConverter;
        private readonly AssigneeContractConverter _assigneeContractConverter;
        private readonly ICurrentUser _currentUser;
        private readonly BusinessTypeProcessMapping _businessTypeMapping;
        private readonly FlowableOptions _flowableOptions;
        private readonly ILogger<ProcessLifecycleAppService> _logger;
        private readonly IDistributedLockService _distributedLockService;
        private readonly IWorkflowReliabilityStore _reliabilityStore;
        private readonly Dm8Options _dm8Options;

        public ProcessLifecycleAppService(
            IFlowableRuntimeService runtimeService,
            IFlowableTaskService taskService,
            IFlowableRepositoryService repositoryService,
            IElasticSearchService esService,
            IProcessSlotConfigProvider slotConfigProvider,
            SlotVariableConverter slotConverter,
            AssigneeContractConverter assigneeContractConverter,
            ICurrentUser currentUser,
            IOptions<BusinessTypeProcessMapping> businessTypeMapping,
            IOptions<FlowableOptions> flowableOptions,
            ILogger<ProcessLifecycleAppService> logger,
            IDistributedLockService distributedLockService,
            IWorkflowReliabilityStore reliabilityStore,
            IOptions<Dm8Options> dm8Options)
        {
            _runtimeService       = runtimeService;
            _taskService          = taskService;
            _repositoryService    = repositoryService;
            _esService            = esService;
            _slotConfigProvider   = slotConfigProvider;
            _slotConverter        = slotConverter;
            _assigneeContractConverter = assigneeContractConverter;
            _currentUser          = currentUser;
            _businessTypeMapping  = businessTypeMapping.Value;
            _flowableOptions      = flowableOptions.Value;
            _logger               = logger;
            _distributedLockService = distributedLockService;
            _reliabilityStore = reliabilityStore;
            _dm8Options = dm8Options.Value;
        }

        // ═══════════════════════════════════════════════════════════
        // StartProcessAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 启动流程
        ///
        /// 执行步骤：
        ///   1. 参数校验
        ///   2. businessType → processDefinitionKey 映射
        ///   3. 查首节点 Slot 定义，将 InitialSlotSelections 转换为 Flowable 变量
        ///   3b. 将 AssigneeContract 展开为推荐人快照，不生成 Flowable 变量
        ///   4. 注入框架内置变量（frameworkCallbackUrl / businessId / processDefinitionKey）
        ///   5. 调 Flowable StartProcessInstance（变量已含首节点 assignee，Flowable 自动绑定）
        ///   6. 写 ES ProcessMetadataDocument
        ///   7. 查首任务信息（用于响应，让调用方减少一次 RTT）
        ///   8. 从 ES nodeSemanticMap 补充首任务的 nodeSemantic / pageCode
        /// </summary>
        public async Task<StartProcessResponse> StartProcessAsync(StartProcessRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.BusinessType))
                throw new BusinessException("businessType 不能为空");

            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new BusinessException("businessId 不能为空");

            var createdBy = _currentUser.UserId;
            if (string.IsNullOrWhiteSpace(createdBy))
                throw new BusinessException("无法确定当前操作人，请先完成登录");

            var lockKey = $"flow:start:{request.BusinessId}";
            var lockValue = Guid.NewGuid().ToString("N");
            var lockAcquired = false;
            try
            {
                lockAcquired = await _distributedLockService.TryAcquireAsync(
                    lockKey,
                    lockValue,
                    TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) when (_dm8Options.Enabled)
            {
                _logger.LogWarning(
                    ex,
                    "Redis advisory lock is unavailable; continuing with DM8 uniqueness. BusinessId={BusinessId}",
                    request.BusinessId);
                // Redis is a performance guard only. DM8's unique BUSINESS_ID
                // constraint remains the final duplicate-start arbiter.
                lockAcquired = true;
            }

            if (!lockAcquired)
            {
                _logger.LogWarning(
                    "获取流程启动锁失败，疑似重复提交: BusinessId={BusinessId}, CreatedBy={CreatedBy}",
                    request.BusinessId,
                    createdBy);

                throw new BusinessException(
                    $"业务 [{request.BusinessId}] 正在启动流程中，请勿重复提交");
            }

            try
            {
                // 1. 校验是否已存在运行中的流程实例
                var existingRunning = _dm8Options.Enabled
                    ? null
                    : await _esService.GetProcessMetadataByBusinessIdAsync(
                        request.BusinessId);

                if (existingRunning != null)
                {
                    _logger.LogWarning(
                        "拒绝重复启动流程: BusinessId={BusinessId}, ExistingProcessInstanceId={ProcessInstanceId}, CreatedBy={CreatedBy}",
                        request.BusinessId,
                        existingRunning.ProcessInstanceId,
                        createdBy);

                    throw new BusinessException(
                        $"业务 [{request.BusinessId}] 已存在运行中流程，不能重复启动");
                }

                // 2. businessType -> processDefinitionKey
                var processDefinitionKey = _businessTypeMapping.GetProcessDefinitionKey(
                    request.BusinessType);

                if (string.IsNullOrWhiteSpace(processDefinitionKey))
                    throw new BusinessException(
                        $"businessType [{request.BusinessType}] 未配置对应的流程定义");

                if (_dm8Options.Enabled)
                {
                    var reservation = await _reliabilityStore.ReserveBusinessAsync(
                        new ReserveBusinessCommand
                        {
                            BusinessId = request.BusinessId,
                            BusinessType = request.BusinessType,
                            ProcessDefinitionKey = processDefinitionKey,
                            CallbackConfigSnapshot = request.Callback == null
                                ? null
                                : JsonSerializer.Serialize(request.Callback)
                        });
                    if (!reservation.Created)
                    {
                        if (!string.IsNullOrWhiteSpace(
                                reservation.Instance.ProcessInstanceId))
                        {
                            return await BuildExistingStartResponseAsync(
                                request.BusinessId,
                                reservation.Instance.ProcessInstanceId);
                        }

                        if (reservation.Instance.FlowState
                            is "starting" or "reconcile_required")
                        {
                            var candidates = await _runtimeService
                                .QueryProcessInstancesByBusinessKeyAsync(
                                    request.BusinessId);
                            if (candidates.Count == 1)
                            {
                                var existing = candidates[0];
                                await _reliabilityStore.BindStartedProcessAsync(
                                    request.BusinessId,
                                    existing.Id,
                                    ParseProcessDefinitionVersion(
                                        existing.ProcessDefinitionId));
                                return await BuildExistingStartResponseAsync(
                                    request.BusinessId,
                                    existing.Id);
                            }
                            if (candidates.Count > 1)
                            {
                                await _reliabilityStore.MarkBusinessFlowStateAsync(
                                    request.BusinessId,
                                    "reconcile_required");
                                throw new BusinessException(
                                    $"业务 [{request.BusinessId}] 在 Flowable 中存在多个运行实例，需要人工对账",
                                    "PROCESS_RECONCILE_MULTIPLE_MATCHES");
                            }
                        }
                        throw new BusinessException(
                            $"Business [{request.BusinessId}] already has flow state [{reservation.Instance.FlowState}].",
                            "PROCESS_ALREADY_RESERVED");
                    }
                }

                _logger.LogInformation(
                    "开始启动流程: BusinessType={BusinessType}, ProcessDefinitionKey={ProcessDefinitionKey}, BusinessId={BusinessId}, CreatedBy={CreatedBy}",
                    request.BusinessType,
                    processDefinitionKey,
                    request.BusinessId,
                    createdBy);

                int? startDefinitionVersion = null;
                if (_dm8Options.Enabled)
                {
                    var latestDefinition = await _repositoryService
                        .GetLatestProcessDefinitionByKeyAsync(
                            processDefinitionKey);
                    startDefinitionVersion = latestDefinition.Version;
                }

                // 3. InitialSlotSelections is the only start-time source for Flowable assignee variables.
                var initConversionResult = await ConvertInitialSlotsAsync(
                    request.InitialSlotSelections,
                    processDefinitionKey,
                    startDefinitionVersion);

                // 3b. AssigneeContract is only expanded into the recommended-assignee snapshot.
                var contractSnapshot = new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase);
                if (request.AssigneeContract?.Roles?.Any() == true)
                {
                    try
                    {
                        var semanticMap = await _slotConfigProvider
                            .GetNodeSemanticMapAsync(
                                processDefinitionKey,
                                startDefinitionVersion);

                        contractSnapshot = _assigneeContractConverter
                            .ToRecommendedSnapshot(request.AssigneeContract, semanticMap);

                        _logger.LogInformation(
                            "AssigneeContract expanded into recommended snapshot. BusinessId={BusinessId}, SlotKeys={Count}",
                            request.BusinessId,
                            contractSnapshot.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "AssigneeContract recommended snapshot expansion failed and will be skipped. BusinessId={BusinessId}",
                            request.BusinessId);
                    }
                }

                if (_dm8Options.Enabled)
                {
                    await _reliabilityStore.UpdateRecommendedAssigneesSnapshotAsync(
                        request.BusinessId,
                        JsonSerializer.Serialize(contractSnapshot));
                }

                // 4. 构建启动变量
                var variables = BuildStartVariables(
                    request,
                    processDefinitionKey,
                    initConversionResult.Variables);

                // 5. 调用 Flowable 启动流程
                FlowableProcessInstance processInstance;
                try
                {
                    processInstance = await _runtimeService.StartProcessInstanceByKeyAsync(
                        processDefinitionKey,
                        request.BusinessId,
                        variables);
                }
                catch (Exception ex)
                {
                    if (_dm8Options.Enabled)
                    {
                        await _reliabilityStore.MarkBusinessFlowStateAsync(
                            request.BusinessId,
                            "reconcile_required");
                    }

                    _logger.LogError(
                        ex,
                        "Flowable 启动流程失败: ProcessDefinitionKey={ProcessDefinitionKey}, BusinessId={BusinessId}",
                        processDefinitionKey,
                        request.BusinessId);

                    throw new BusinessException(
                        $"启动流程失败: {ex.Message}",
                        "FLOWABLE_START_FAILED");
                }

                if (processInstance == null || string.IsNullOrWhiteSpace(processInstance.Id))
                {
                    if (_dm8Options.Enabled)
                    {
                        await _reliabilityStore.MarkBusinessFlowStateAsync(
                            request.BusinessId,
                            "reconcile_required");
                    }
                    _logger.LogError(
                        "Flowable 启动流程返回空实例: ProcessDefinitionKey={ProcessDefinitionKey}, BusinessId={BusinessId}",
                        processDefinitionKey,
                        request.BusinessId);

                    throw new BusinessException("启动流程失败：未获取到流程实例 ID");
                }

                _logger.LogInformation(
                    "Flowable 流程启动成功: ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
                    processInstance.Id,
                    request.BusinessId);

                if (_dm8Options.Enabled)
                {
                    try
                    {
                        var resolvedDefinitionVersion =
                            ParseProcessDefinitionVersion(
                                processInstance.ProcessDefinitionId)
                            ?? startDefinitionVersion;
                        _logger.LogInformation(
                            "Binding started process to DM8. BusinessId={BusinessId}, ProcessDefinitionId={ProcessDefinitionId}, ResolvedVersion={ResolvedVersion}",
                            request.BusinessId,
                            processInstance.ProcessDefinitionId,
                            resolvedDefinitionVersion);
                        await _reliabilityStore.BindStartedProcessAsync(
                            request.BusinessId,
                            processInstance.Id,
                            resolvedDefinitionVersion);
                    }
                    catch
                    {
                        await _reliabilityStore.MarkBusinessFlowStateAsync(
                            request.BusinessId,
                            "reconcile_required");
                        throw;
                    }
                }

                // 6. Write ES metadata
                var esDocument = BuildProcessMetadataDocument(
                    processInstance,
                    request,
                    processDefinitionKey,
                    createdBy,
                    contractSnapshot,
                    ParseProcessDefinitionVersion(
                        processInstance.ProcessDefinitionId)
                    ?? startDefinitionVersion);

                try
                {
                    await _esService.IndexProcessMetadataAsync(esDocument);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[ES_WRITE_FAIL] Process started in Flowable but ES metadata write failed. Retrying once. ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
                        processInstance.Id,
                        request.BusinessId);

                    try
                    {
                        await Task.Delay(500);
                        await _esService.IndexProcessMetadataAsync(esDocument);

                        _logger.LogWarning(
                            "[ES_WRITE_RETRY_SUCCESS] ES metadata write retry succeeded. ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
                            processInstance.Id,
                            request.BusinessId);
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogCritical(
                            retryEx,
                            "[ES_WRITE_ORPHAN] Process instance exists in Flowable but ES metadata write failed twice. ProcessInstanceId={ProcessInstanceId}, BusinessId={BusinessId}",
                            processInstance.Id,
                            request.BusinessId);

                        if (!_dm8Options.Enabled)
                        {
                            throw new BusinessException(
                                $"Process started (ProcessInstanceId={processInstance.Id}), but metadata write failed. Please repair ES metadata.",
                                "PROCESS_METADATA_INDEX_ORPHAN");
                        }
                    }
                }
                _logger.LogInformation(
                    "ES 元数据写入成功: ProcessInstanceId={ProcessInstanceId}",
                    processInstance.Id);

                // 7. 查询首任务
                FlowableTask firstTask = null;
                try
                {
                    var firstTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
                    {
                        ProcessInstanceId = processInstance.Id
                    });

                    firstTask = firstTasks?.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "查询首任务失败（不影响启动结果）: ProcessInstanceId={ProcessInstanceId}",
                        processInstance.Id);
                }

                if (firstTask == null)
                {
                    _logger.LogWarning(
                        "流程启动后未找到首任务，流程可能已自动完成: ProcessInstanceId={ProcessInstanceId}",
                        processInstance.Id);
                }

                // 8. 查询首节点语义
                string firstNodeSemantic = null;
                string firstPageCode = null;

                if (firstTask != null && !string.IsNullOrWhiteSpace(firstTask.TaskDefinitionKey))
                {
                    try
                    {
                        var semanticMap = await _slotConfigProvider
                            .GetNodeSemanticMapAsync(processDefinitionKey);

                        if (semanticMap != null &&
                            semanticMap.TryGetValue(firstTask.TaskDefinitionKey, out var nodeInfo) &&
                            nodeInfo != null)
                        {
                            firstNodeSemantic = nodeInfo.NodeSemantic;
                            firstPageCode = nodeInfo.PageCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "查询首节点语义信息失败（不影响启动结果）: ProcessDefinitionKey={ProcessDefinitionKey}, TaskDefinitionKey={TaskDefinitionKey}",
                            processDefinitionKey,
                            firstTask.TaskDefinitionKey);
                    }
                }

                // 9. 返回启动结果
                return new StartProcessResponse
                {
                    ProcessInstanceId = processInstance.Id,
                    BusinessId = request.BusinessId,
                    FirstTaskId = firstTask?.Id,
                    FirstNodeSemantic = firstNodeSemantic,
                    FirstPageCode = firstPageCode
                };
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        await _distributedLockService.ReleaseAsync(lockKey, lockValue);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "释放流程启动锁失败: LockKey={LockKey}, BusinessId={BusinessId}",
                            lockKey,
                            request.BusinessId);
                    }
                }
            }
        }
        // ═══════════════════════════════════════════════════════════
        // TerminateProcessAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 终止流程（管理员强制终止）
        ///
        /// 执行步骤：
        ///   1. 从 ES 查流程元数据
        ///   2. 调 Flowable DeleteProcessInstance
        ///   3. 更新 ES status = terminated
        ///
        /// 注意：终止是不可逆操作，不写 ProcessAuditRecord（非审批动作）
        /// </summary>
        public async Task TerminateProcessAsync(TerminateProcessRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new BusinessException("businessId 不能为空");
            if (string.IsNullOrWhiteSpace(request.Reason))
                throw new BusinessException("reason 不能为空");

            _logger.LogInformation(
                "终止流程: BusinessId={BusinessId}, Reason={Reason}",
                request.BusinessId, request.Reason);

            WorkflowBusinessInstance? binding = null;
            ProcessMetadataDocument? metadata = null;
            if (_dm8Options.Enabled)
            {
                binding = await _reliabilityStore.GetBusinessByBusinessIdAsync(
                    request.BusinessId);
                if (binding == null
                    || string.IsNullOrWhiteSpace(binding.ProcessInstanceId))
                    throw new BusinessException(
                        $"DM8 中未找到业务 ID 对应的流程绑定: {request.BusinessId}",
                        "PROCESS_BINDING_NOT_FOUND");
            }
            else
            {
                metadata = await _esService.GetProcessMetadataByBusinessIdAsync(
                    request.BusinessId);
                if (metadata == null)
                    throw new BusinessException(
                        $"未找到业务 ID 对应的运行中流程: {request.BusinessId}");
            }

            var processInstanceId =
                binding?.ProcessInstanceId ?? metadata!.ProcessInstanceId;
            WorkflowTaskAction? action = null;
            if (_dm8Options.Enabled)
            {
                var key = $"process:terminate:{processInstanceId}";
                action = await _reliabilityStore.PrepareTaskActionAsync(
                    new PrepareTaskActionCommand
                    {
                        ActionId = StableId(key),
                        IdempotencyKey = key,
                        BusinessId = request.BusinessId,
                        ProcessInstanceId = processInstanceId,
                        ActionType = "terminate",
                        OperatorId = _currentUser.UserId ?? "system",
                        RequestJson = JsonSerializer.Serialize(request)
                    });
                if (action.ResultState == "applied")
                    return;
            }

            // 调 Flowable 删除流程实例
            try
            {
                await _runtimeService.DeleteProcessInstanceAsync(
                    processInstanceId, request.Reason);
                if (action != null)
                {
                    await _reliabilityStore.MarkTaskActionResultAsync(
                        action.ActionId, "applied", "deleted", null);
                    await _reliabilityStore.MarkBusinessFlowStateAsync(
                        request.BusinessId, "terminated");
                }
            }
            catch (Exception ex)
            {
                if (action != null)
                {
                    await _reliabilityStore.MarkTaskActionResultAsync(
                        action.ActionId,
                        "reconcile_required",
                        "unknown",
                        ex.Message);
                }
                throw;
            }

            // 更新 ES 状态
            try
            {
                await _esService.UpdateProcessStatusAsync(
                    processInstanceId,
                    "terminated",
                    DateTime.UtcNow);
            }
            catch when (_dm8Options.Enabled)
            {
                _logger.LogWarning(
                    "ES termination projection failed; DM8 action is authoritative. ProcessInstanceId={ProcessInstanceId}",
                    processInstanceId);
            }

            _logger.LogInformation(
                "流程已终止: ProcessInstanceId={ProcessInstanceId}",
                processInstanceId);
        }

        // ═══════════════════════════════════════════════════════════
        // 私有辅助方法
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 获取指定流程定义下所有节点的 Slot 定义合并列表
        /// 用于首节点 Slot 转换——调用方传的 slotKey 只要在整个流程定义中存在即可匹配
        ///
        /// 设计说明：
        ///   启动时不要求调用方传 initialNodeKey，
        ///   而是把 InitialSlotSelections 的 slotKey 与全流程所有 Slot 做匹配，
        ///   找到对应的 variableName 后转换为变量。
        ///   这样调用方无需感知首节点的 taskDefinitionKey。
        /// </summary>
        private async Task<List<SlotDefinition>> GetAllSlotDefsForProcessAsync(
            string processDefinitionKey)
        {
            var semanticMap = await _slotConfigProvider
                .GetNodeSemanticMapAsync(processDefinitionKey);

            if (semanticMap == null || !semanticMap.Any())
            {
                _logger.LogWarning(
                    "未找到流程定义的节点语义配置: {ProcessDefinitionKey}，" +
                    "InitialSlotSelections 将无法转换为 Flowable 变量",
                    processDefinitionKey);
                return new List<SlotDefinition>();
            }

            // 合并所有节点的 Slot 定义，去重（以 slotKey 为唯一键）
            var allSlots = semanticMap.Values
                .Where(n => n.Slots != null)
                .SelectMany(n => n.Slots)
                .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            return allSlots;
        }

        private async Task<SlotConversionResult> ConvertInitialSlotsAsync(
            List<SlotSelection> selections,
            string processDefinitionKey,
            int? processDefinitionVersion = null)
        {
            if (selections == null || !selections.Any())
                return new SlotConversionResult();

            // 从全局 Slot 中找 slotKey → variableName 的映射
            var semanticMap = await _slotConfigProvider
                .GetNodeSemanticMapAsync(
                    processDefinitionKey,
                    processDefinitionVersion);

            // 构建全局 slotKey → SlotDefinition 查找表
            var slotLookup = semanticMap.Values
                .Where(n => n.Slots != null)
                .SelectMany(n => n.Slots)
                .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var result = new SlotConversionResult();

            foreach (var selection in selections)
            {
                if (!slotLookup.TryGetValue(selection.SlotKey, out var def))
                {
                    throw new BusinessException(
                        $"initialSlotSelections 中的 slotKey [{selection.SlotKey}] 未在流程定义中注册",
                        "SLOT_UNKNOWN");
                }

                if (selection.Users == null || !selection.Users.Any())
                {
                    throw new BusinessException(
                        $"slotKey [{selection.SlotKey}] 未传入用户",
                        "SLOT_REQUIRED");
                }

                if (string.IsNullOrWhiteSpace(def.RoleKey))
                    throw new BusinessException(
                        $"Slot [{def.Label}]（{def.SlotKey}）roleKey 不能为空",
                        "SLOT_ROLE_KEY_REQUIRED");

                if (string.IsNullOrWhiteSpace(def.VariableName))
                    throw new BusinessException(
                        $"Slot [{def.Label}]（{def.SlotKey}）variableName 不能为空",
                        "SLOT_VARIABLE_NAME_REQUIRED");

                // 转换变量
                if (def.Mode == "single")
                    result.Variables[def.VariableName] = selection.Users[0];
                else
                    result.Variables[def.VariableName] = selection.Users;

                result.Snapshots.Add(new SlotSelectionSnapshot
                {
                    SlotKey = def.SlotKey,
                    Label = def.Label,
                    Users = selection.Users
                });
            }

            return result;
        }

        /// <summary>
        /// 构建 Flowable 启动变量
        ///
        /// 变量优先级（高→低）：
        ///   框架内置变量 > InitialSlotSelections 转换变量 > 业务变量
        ///
        /// AssigneeContract 不生成任何 Flowable 变量；
        /// 它只写入 RecommendedAssigneesSnapshot 供前端初始化选人区。
        /// </summary>
        private Dictionary<string, object> BuildStartVariables(
            StartProcessRequest request,
            string processDefinitionKey,
            Dictionary<string, object> slotVariables)
        {
            // 从业务变量开始（最低优先级）
            var variables = new Dictionary<string, object>(
                request.BusinessVariables ?? new Dictionary<string, object>());

            // Slot 转换变量覆盖业务变量
            foreach (var kv in slotVariables)
                variables[kv.Key] = kv.Value;

            // 框架内置变量（最高优先级，不可被覆盖）
            if (!string.IsNullOrWhiteSpace(_flowableOptions.FrameworkCallbackUrl))
            {
                variables["frameworkCallbackUrl"] = _flowableOptions.FrameworkCallbackUrl;
            }
            else
            {
                _logger.LogWarning(
                    "未配置 FrameworkCallbackUrl，流程完成后将不会触发回调");
            }

            variables["businessId"]           = request.BusinessId;
            variables["processDefinitionKey"] = processDefinitionKey;

            _logger.LogDebug(
                "启动变量构建完成: BusinessId={BusinessId}, 变量Keys=[{Keys}]",
                request.BusinessId, string.Join(", ", variables.Keys));

            return variables;
        }

        /// <summary>
        /// 构建 ES 流程元数据文档
        /// </summary>
        private ProcessMetadataDocument BuildProcessMetadataDocument(
            FlowableProcessInstance processInstance,
            StartProcessRequest request,
            string processDefinitionKey,
            string createdBy,
            Dictionary<string, List<string>> contractSnapshot,
            int? processDefinitionVersion)
        {
            CallbackMetadata callbackMetadata = null;
            if (request.Callback != null
                && !string.IsNullOrWhiteSpace(request.Callback.Url))
            {
                callbackMetadata = new CallbackMetadata
                {
                    Url            = request.Callback.Url,
                    TimeoutSeconds = request.Callback.TimeoutSeconds,
                    RetryCount     = request.Callback.RetryCount,
                    Headers        = request.Callback.Headers
                                     ?? new Dictionary<string, string>()
                };
            }

            return new ProcessMetadataDocument
            {
                Id                   = processInstance.Id,
                ProcessInstanceId    = processInstance.Id,
                ProcessDefinitionKey = processDefinitionKey,
                ProcessDefinitionVersion = processDefinitionVersion,
                BusinessId           = request.BusinessId,
                BusinessType         = request.BusinessType,
                Status               = "running",
                CreatedBy            = createdBy,
                CreatedTime          = DateTime.UtcNow,
                Callback             = callbackMetadata,
                // NodeSemanticMap 不在启动时写入
                // 它在部署 BPMN 时由 BpmnDeploymentAppService 写入 ProcessDefinitionSemanticDocument
                // 查询时从 ProcessDefinitionSemanticDocument 读取，不存在于实例文档中
                NodeSemanticMap      = new Dictionary<string, NodeSemanticInfo>(),
                RecommendedAssigneesSnapshot = contractSnapshot
                    ?? new Dictionary<string, List<string>>()
            };
        }

        private static int? ParseProcessDefinitionVersion(
            string processDefinitionId)
        {
            if (string.IsNullOrWhiteSpace(processDefinitionId))
                return null;
            var parts = processDefinitionId.Split(':');
            return parts.Length >= 3
                   && int.TryParse(parts[^2], out var version)
                ? version
                : null;
        }

        private async Task<StartProcessResponse> BuildExistingStartResponseAsync(
            string businessId,
            string processInstanceId)
        {
            FlowableTask? firstTask = null;
            try
            {
                firstTask = (await _taskService.QueryTasksAsync(
                    new FlowableTaskQuery
                    {
                        ProcessInstanceId = processInstanceId,
                        Size = 1
                    })).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Idempotent start response could not load current task. ProcessInstanceId={ProcessInstanceId}",
                    processInstanceId);
            }
            return new StartProcessResponse
            {
                BusinessId = businessId,
                ProcessInstanceId = processInstanceId,
                FirstTaskId = firstTask?.Id
            };
        }

        private static string StableId(string value)
            => Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
    }
}
