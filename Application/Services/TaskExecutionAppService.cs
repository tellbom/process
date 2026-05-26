using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Services
{
    /// <summary>
    /// 任务执行服务
    ///
    /// 职责：
    ///   ✔ 完成任务（通过 / 驳回）
    ///   ✔ 查询当前用户的待办任务
    ///   ✔ 转派任务
    ///
    /// 核心设计约束：
    ///   - complete 之后直接返回，不轮询，不补 assignee
    ///   - 驳回使用 ChangeActivityStateAsync 跳转，不走 CompleteAsync，不改 BPMN
    ///   - Slot→变量转换 + 一次 CompleteAsync，Flowable 自己推进并 assign 下一任务
    ///   - 返回 nodeSemantic + pageCode，不返回 JumpUrl / RequiredSlots
    /// </summary>
    public class TaskExecutionAppService
    {
        private readonly IFlowableRuntimeService _runtimeService;
        private readonly IFlowableTaskService _taskService;
        private readonly IElasticSearchService _esService;
        private readonly IProcessSlotConfigProvider _slotConfigProvider;
        private readonly SlotVariableConverter _slotConverter;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<TaskExecutionAppService> _logger;
        private readonly ProcessCallbackAppService _callbackService;

        public TaskExecutionAppService(
            IFlowableRuntimeService runtimeService,
            IFlowableTaskService taskService,
            IElasticSearchService esService,
            IProcessSlotConfigProvider slotConfigProvider,
            SlotVariableConverter slotConverter,
            ICurrentUser currentUser,
            ILogger<TaskExecutionAppService> logger,
            ProcessCallbackAppService callbackService)
        {
            _runtimeService  = runtimeService;
            _taskService     = taskService;
            _esService       = esService;
            _slotConfigProvider = slotConfigProvider;
            _slotConverter   = slotConverter;
            _currentUser     = currentUser;
            _logger          = logger;
            _callbackService = callbackService;
        }

        // ═══════════════════════════════════════════════════════════
        // CompleteTaskAsync
        // ═══════════════════════════════════════════════════════════

        public async Task<CompleteTaskResponse> CompleteTaskAsync(CompleteTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new BusinessException("businessId 不能为空");

            if (request.Action == ApprovalAction.Reject)
            {
                if (string.IsNullOrWhiteSpace(request.RejectCode))
                    throw new BusinessException("驳回时 rejectCode 不能为空", "REJECT_CODE_REQUIRED");
                if (string.IsNullOrWhiteSpace(request.RejectReason))
                    throw new BusinessException("驳回时 rejectReason 不能为空", "REJECT_REASON_REQUIRED");
            }

            var operatorId = ResolveOperatorId(request.EmployeeId);

            _logger.LogInformation(
                "开始处理任务: BusinessId={BusinessId}, OperatorId={OperatorId}, Action={Action}",
                request.BusinessId, operatorId, request.Action);

            // ── Step 1: 查流程元数据 ───────────────────────────────
            var metadata = await _esService.GetProcessMetadataByBusinessIdAsync(
                request.BusinessId);
            if (metadata == null)
                throw new BusinessException(
                    $"未找到业务 ID 对应的运行中流程: {request.BusinessId}");

            // ── Step 2: 定位任务 ───────────────────────────────────
            FlowableTask myTask = !string.IsNullOrWhiteSpace(request.TaskId)
                ? await ResolveTaskByIdAsync(
                    request.TaskId, metadata.ProcessInstanceId, operatorId)
                : await FindTaskForOperatorAsync(
                    metadata.ProcessInstanceId, operatorId);

            _logger.LogInformation(
                "定位到待办任务: TaskId={TaskId}, TaskDefinitionKey={Key}",
                myTask.Id, myTask.TaskDefinitionKey);

            // ── Step 3: Claim（候选人模式）────────────────────────
            if (string.IsNullOrWhiteSpace(myTask.Assignee))
            {
                await _taskService.ClaimAsync(myTask.Id, operatorId);
                _logger.LogInformation("任务已认领: TaskId={TaskId}", myTask.Id);
            }

            // ── Step 4: 驳回走独立路径（不走 CompleteAsync）────────
            // 驳回使用 ChangeActivityStateAsync 跳转到目标节点
            // 流程仍在 running，ES status 保持不变
            if (request.Action == ApprovalAction.Reject)
                return await HandleRejectAsync(request, myTask, metadata, operatorId);

            // ── Step 5: 组装变量 ──────────────────────────────────
            var (variables, slotSnapshots) = await BuildCompletionVariablesAsync(
                request, myTask, metadata, operatorId);

            // ── Step 6: 写审计记录（先于 CompleteAsync）──────────
            // 必须在 CompleteAsync 之前写入 ES：
            // Flowable CompleteAsync 会同步执行 BPMN 中的 HTTP ServiceTask（节点回调）
            // 回调触发时 ProcessCallbackAppService.BuildNodeContextAsync 会查询 ES 审计记录
            // 若审计写在 CompleteAsync 之后，回调取到的 lastAuditRecord 始终为空
            //
            // 已知副作用：若 CompleteAsync 失败（Flowable 返回错误），
            // 审计记录已写入但任务未完成，ES 中存在一条幽灵审计记录
            // 此代价可接受（审计记录多余 vs 回调上下文永远为空，前者更轻）
            await WriteAuditRecordSafeAsync(
                metadata, myTask, request, operatorId, slotSnapshots);

            // ── Step 7: CompleteAsync（触发 Flowable 推进）──────────
            await _taskService.CompleteAsync(myTask.Id, variables);

            _logger.LogInformation(
                "任务完成: TaskId={TaskId}, Action={Action}, 注入变量数={VarCount}",
                myTask.Id, request.Action, variables.Count);

            // ── Step 8a: 读取流程实例变量（含多实例上下文）──────────
            // Flowable 多实例节点执行时自动写入 nrOfInstances / nrOfCompletedInstances /
            // nrOfActiveInstances，需从实例变量中读取（不在 complete 时注入的 variables 中）
            // 失败只记 Debug 日志，multiInstance 上下文降级为 enabled=false
            Dictionary<string, object> processVariables = null;
            try
            {
                processVariables = await _runtimeService
                    .GetProcessVariablesAsync(myTask.ProcessInstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "读取流程实例变量失败，multiInstance 上下文将为 enabled=false: TaskId={TaskId}",
                    myTask.Id);
            }

            // ── Step 8b: 主动节点回调 ─────────────────────────────────
            // 触发依据：slotConfig.callbackUrl 是否有值（流程中心不判断节点类型）
            // 失败只记 Error 日志，不阻塞主流程返回
            await _callbackService.SendNodeCompletedCallbackSafeAsync(
                metadata,
                myTask.TaskDefinitionKey,
                processVariables);

            return new CompleteTaskResponse { Success = true, Message = "审批通过" };
        }

        // ═══════════════════════════════════════════════════════════
        // GetPendingTasksAsync
        // ═══════════════════════════════════════════════════════════

        public async Task<PendingTaskPageResult> GetPendingTasksAsync(
            GetPendingTasksRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.EmployeeId))
                throw new BusinessException("employeeId 不能为空");

            var assigneeTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                Assignee = request.EmployeeId
            });
            var candidateTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                CandidateUser = request.EmployeeId
            });

            var allTasks = assigneeTasks
                .Concat(candidateTasks)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderByDescending(t => t.CreateTime)
                .ToList();

            if (!allTasks.Any())
                return new PendingTaskPageResult
                {
                    PageIndex = request.PageIndex,
                    PageSize = request.PageSize
                };

            var processInstanceIds = allTasks
                .Select(t => t.ProcessInstanceId)
                .Distinct()
                .ToList();
            var metadataDict = await _esService.GetProcessMetadataBatchAsync(processInstanceIds);

            var semanticMapCache = new Dictionary<string, Dictionary<string, NodeSemanticInfo>>(
                StringComparer.OrdinalIgnoreCase);

            var result = new List<PendingTaskDto>();

            foreach (var task in allTasks)
            {
                if (!metadataDict.TryGetValue(task.ProcessInstanceId, out var meta))
                    continue;

                if (!string.IsNullOrWhiteSpace(request.BusinessType)
                    && !string.Equals(meta.BusinessType, request.BusinessType,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!semanticMapCache.TryGetValue(meta.ProcessDefinitionKey, out var semanticMap))
                {
                    semanticMap = await _slotConfigProvider
                        .GetNodeSemanticMapAsync(meta.ProcessDefinitionKey);
                    semanticMapCache[meta.ProcessDefinitionKey] = semanticMap;
                }

                semanticMap.TryGetValue(task.TaskDefinitionKey, out var nodeInfo);

                var slotRecommendedUsers = new Dictionary<string, List<string>>();
                var restrictMap = new Dictionary<string, bool>();
                if (nodeInfo?.Slots != null)
                {
                    foreach (var slot in nodeInfo.Slots)
                    {
                        if (string.IsNullOrWhiteSpace(slot.RoleKey))
                            throw new BusinessException(
                                $"节点 [{task.TaskDefinitionKey}] Slot [{slot.SlotKey}] roleKey 不能为空",
                                "SLOT_ROLE_KEY_REQUIRED");

                        restrictMap[slot.SlotKey] = slot.RestrictToRecommended;

                        if (meta.RecommendedAssigneesSnapshot?.TryGetValue(
                                slot.RoleKey, out var slotRecommended) == true
                            && slotRecommended?.Any() == true)
                        {
                            slotRecommendedUsers[slot.SlotKey] = slotRecommended;
                        }
                    }
                }

                result.Add(new PendingTaskDto
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    BusinessId = meta.BusinessId,
                    BusinessType = meta.BusinessType,
                    NodeSemantic = nodeInfo?.NodeSemantic,
                    RoleKey = nodeInfo?.RoleKey,
                    PageCode = nodeInfo?.PageCode,
                    PageUrl = BuildPageUrl(
                        nodeInfo?.PageCode,
                        meta.BusinessId,
                        task.Id,
                        meta.BusinessType,
                        task.TaskDefinitionKey,
                        nodeInfo?.NodeSemantic),
                    CanReject = nodeInfo.CanReject,
                    RejectOptions = nodeInfo.RejectOptions,
                    RequiredSlots = nodeInfo?.Slots ?? new List<SlotDefinition>(),
                    // 前端通过 pageCode → COMPONENT_REGISTRY 找到表单组件，
                    // 表单组件自己知道要选哪些人
                    IsAfterConvergencePoint = nodeInfo?.IsConvergencePoint ?? false,
                    CreateTime = task.CreateTime,
                    Priority = task.Priority,
                    SlotRecommendedUsers = slotRecommendedUsers,
                    RestrictToRecommended = restrictMap
                });
            }

            var total = result.Count;
            var paged = result
                .Skip((request.PageIndex - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            return new PendingTaskPageResult
            {
                Items = paged,
                Total = total,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize
            };
        }

        // ═══════════════════════════════════════════════════════════
        // ReassignTaskAsync
        // ═══════════════════════════════════════════════════════════

        private static string BuildPageUrl(
            string pageCode,
            string businessId,
            string taskId,
            string businessType,
            string nodeId,
            string nodeSemantic)
        {
            if (string.IsNullOrWhiteSpace(pageCode)
                || !Uri.TryCreate(pageCode, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            var builder = new UriBuilder(uri);
            var parameters = new List<string>();

            if (!string.IsNullOrWhiteSpace(builder.Query))
                parameters.Add(builder.Query.TrimStart('?'));

            AddQueryParameter(parameters, "businessId", businessId);
            AddQueryParameter(parameters, "taskId", taskId);
            AddQueryParameter(parameters, "businessType", businessType);
            AddQueryParameter(parameters, "nodeId", nodeId);
            AddQueryParameter(parameters, "nodeSemantic", nodeSemantic);

            builder.Query = string.Join("&", parameters);
            return builder.Uri.ToString();
        }

        private static void AddQueryParameter(
            List<string> parameters,
            string key,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            parameters.Add(
                $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        public async Task ReassignTaskAsync(ReassignTaskRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new BusinessException("businessId 不能为空");
            if (request.NewAssignees == null || !request.NewAssignees.Any())
                throw new BusinessException("newAssignees 不能为空");

            var metadata = await _esService.GetProcessMetadataByBusinessIdAsync(
                request.BusinessId);
            if (metadata == null)
                throw new BusinessException(
                    $"未找到业务 ID 对应的运行中流程: {request.BusinessId}");

            List<FlowableTask> tasksToReassign;

            if (!string.IsNullOrWhiteSpace(request.TaskId))
            {
                var task = await _taskService.GetTaskAsync(request.TaskId);
                if (task == null)
                    throw new BusinessException($"任务不存在: {request.TaskId}");
                if (task.ProcessInstanceId != metadata.ProcessInstanceId)
                    throw new BusinessException("指定任务不属于该流程");
                tasksToReassign = new List<FlowableTask> { task };
            }
            else
            {
                tasksToReassign = await _taskService.QueryTasksAsync(new FlowableTaskQuery
                {
                    ProcessInstanceId = metadata.ProcessInstanceId
                });
                if (!tasksToReassign.Any())
                    throw new BusinessException("该流程下当前没有待办任务");
            }

            foreach (var task in tasksToReassign)
            {
                await _taskService.ClearCandidateUsersAsync(task.Id);

                if (request.NewAssignees.Count == 1)
                {
                    await _taskService.SetAssigneeAsync(task.Id, request.NewAssignees[0]);
                    _logger.LogInformation(
                        "任务转派（单人）: TaskId={TaskId}, NewAssignee={Assignee}",
                        task.Id, request.NewAssignees[0]);
                }
                else
                {
                    await _taskService.SetAssigneeAsync(task.Id, null);
                    await _taskService.AddCandidateUsersAsync(task.Id, request.NewAssignees);
                    _logger.LogInformation(
                        "任务转派（多人候选）: TaskId={TaskId}, Count={Count}",
                        task.Id, request.NewAssignees.Count);
                }

                // 写转派审计记录（失败不影响主流程）
                await WriteReassignAuditRecordSafeAsync(metadata, task, request);
            }

            _logger.LogInformation(
                "转派完成: BusinessId={BusinessId}, Reason={Reason}",
                request.BusinessId, request.Reason);
        }

        // ═══════════════════════════════════════════════════════════
        // HandleRejectAsync（驳回跳转）
        // ═══════════════════════════════════════════════════════════

        private async Task<CompleteTaskResponse> HandleRejectAsync(
            CompleteTaskRequest request,
            FlowableTask currentTask,
            ProcessMetadataDocument metadata,
            string operatorId)
        {
            var semanticMap = await _slotConfigProvider
                .GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey);

            // 补强1：校验当前节点具备驳回能力
            if (!semanticMap.TryGetValue(currentTask.TaskDefinitionKey, out var currentNodeInfo))
                throw new BusinessException(
                    $"未找到节点语义配置: {currentTask.TaskDefinitionKey}");

            if (!currentNodeInfo.CanReject)
                throw new BusinessException(
                    $"节点 [{currentNodeInfo.NodeSemantic}] 不允许发起驳回",
                    "REJECT_NOT_ALLOWED");

            // 补强1：校验 rejectCode 在当前节点的 RejectOptions 中存在
            var matchedOption = currentNodeInfo.RejectOptions?
                .FirstOrDefault(o => string.Equals(
                    o.RejectCode, request.RejectCode, StringComparison.OrdinalIgnoreCase));

            if (matchedOption == null)
                throw new BusinessException(
                    $"节点 [{currentNodeInfo.NodeSemantic}] 不支持驳回模式 [{request.RejectCode}]，" +
                    $"允许值：{string.Join("、", currentNodeInfo.RejectOptions?.Select(o => o.RejectCode) ?? Enumerable.Empty<string>())}",
                    "REJECT_CODE_INVALID");

            // 补强2：根据 rejectCode 全局唯一查找目标节点
            var targetNode = semanticMap.Values.FirstOrDefault(n =>
                n.IsRejectTarget &&
                string.Equals(n.RejectCode, request.RejectCode,
                    StringComparison.OrdinalIgnoreCase));

            if (targetNode == null)
                throw new BusinessException(
                    $"未找到 rejectCode [{request.RejectCode}] 对应的目标节点",
                    "REJECT_TARGET_NOT_FOUND");

            // Step 3: 查当前流程实例所有活动任务，整轮撤销后跳转目标节点
            //
            // 设计说明：
            //   驳回语义 = 本轮审批整体作废，所有在途节点全部取消，回到目标节点重新开始。
            //   cancelActivityIds 传 TaskDefinitionKey（activityId 维度，非 task instance id）：
            //     · 普通节点  → 一 key 对应一 task，正常取消
            //     · 多实例节点（会签/或签）→ 一 key 对应多 task instance，Flowable 统一取消所有实例
            //     · 并行分支  → 多个不同 key，全部收入，整轮清除
            //   Flowable 7.2 已验证：cancelActivityIds 传 activityId 可取消多实例节点所有实例。
            var allActiveTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                ProcessInstanceId = metadata.ProcessInstanceId
            });

            if (!allActiveTasks.Any())
                throw new BusinessException("当前流程下无活动任务，流程可能已结束");

            var cancelActivityIds = allActiveTasks
                .Select(t => t.TaskDefinitionKey)
                .Distinct()
                .ToList();

            // 诊断日志：区分多实例 / 并行 / 普通场景，便于排查
            var multiInstanceKeys = allActiveTasks
                .GroupBy(t => t.TaskDefinitionKey)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (multiInstanceKeys.Any())
                _logger.LogInformation(
                    "驳回整轮撤销，包含多实例节点 [{MultiNodes}]，共取消 {Count} 个 activityId: [{AllNodes}]",
                    string.Join("、", multiInstanceKeys),
                    cancelActivityIds.Count,
                    string.Join("、", cancelActivityIds));
            else if (cancelActivityIds.Count > 1)
                _logger.LogInformation(
                    "驳回整轮撤销（并行分支），共取消 {Count} 个节点: [{Nodes}]",
                    cancelActivityIds.Count, string.Join("、", cancelActivityIds));

            _logger.LogInformation(
                "执行驳回: {CurrentNode} → {TargetNode}, RejectCode={RejectCode}, OperatorId={OperatorId}",
                currentTask.TaskDefinitionKey, targetNode.TaskDefinitionKey,
                request.RejectCode, operatorId);

            await _runtimeService.ChangeActivityStateAsync(
                metadata.ProcessInstanceId,
                cancelActivityIds,
                targetNode.TaskDefinitionKey);

            _logger.LogInformation(
                "驳回跳转成功: {CurrentNode} → {TargetNode}",
                currentTask.TaskDefinitionKey, targetNode.TaskDefinitionKey);

            await WriteAuditRecordSafeAsync(
                metadata, currentTask, request, operatorId,
                new List<SlotSelectionSnapshot>(),
                targetNode.TaskDefinitionKey);

            // 驳回完成后主动通知业务系统
            // 不经过 Flowable HTTP ServiceTask（ChangeActivityStateAsync 是强制跳转，不经过 sequenceFlow）
            // 失败只记日志，不影响驳回结果
            var rejectAuditSnapshot = new AuditRecordSnapshot
            {
                Action       = "reject",
                OperatorId   = operatorId,
                Comment      = request.Comment,
                RejectReason = request.RejectReason,
                RejectCode   = request.RejectCode,
                RejectTargetNodeKey = targetNode.TaskDefinitionKey,
                OperatedAt   = DateTime.UtcNow,
                SlotSelections = new List<SlotSelectionRecord>()
            };
            await _callbackService.SendRejectCallbackSafeAsync(
                metadata,
                currentTask.TaskDefinitionKey,
                targetNode.TaskDefinitionKey,
                rejectAuditSnapshot);

            return new CompleteTaskResponse
            {
                Success = true,
                Message = $"已驳回（{matchedOption.Label}），流程已退回至【{targetNode.NodeSemantic}】"
            };
        }

        // ═══════════════════════════════════════════════════════════
        // BuildCompletionVariablesAsync（仅通过路径使用）
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 组装任务完成时的 Flowable 变量
        ///
        /// ── 关于 NextSlotSelections ──────────────────────────────────────
        /// NextSlotSelections 是唯一最终生效的人员来源：
        ///   - 全自动流程（AssigneeContract）：处理人已在启动时注入为 Flowable 变量，
        ///     正常情况下无需传此字段（Flowable 自动从变量中绑定 assignee）
        ///   - 半自动流程：前端读取 recommendedUsers 初始化选人区，用户确认后提交此字段
        ///   - 旧流程：保持原有逐节点选人行为不变
        ///
        /// Phase 1 不加 [Obsolete]，不删除，不修改任何逻辑。
        /// ──────────────────────────────────────────────────────────────────
        /// </summary>
        private async Task<(Dictionary<string, object> variables,
            List<SlotSelectionSnapshot> snapshots)>
            BuildCompletionVariablesAsync(
                CompleteTaskRequest request,
                FlowableTask currentTask,
                ProcessMetadataDocument metadata,
                string operatorId)
        {
            // ── 1. 业务变量（最低优先级）────────────────────────
            var variables = new Dictionary<string, object>(
                request.BusinessVariables ?? new Dictionary<string, object>());

            // ── 2. Slot → 变量转换 ──────────────────────────────
            List<SlotSelectionSnapshot> snapshots = new();

            if (request.NextSlotSelections?.Any() == true)
            {
                var slotDefs = await _slotConfigProvider.GetSlotsForNodeAsync(
                    metadata.ProcessDefinitionKey,
                    currentTask.TaskDefinitionKey);

                var conversionResult = _slotConverter.Convert(
                    request.NextSlotSelections,
                    slotDefs,
                    request.BusinessVariables);

                foreach (var kv in conversionResult.Variables)
                    variables[kv.Key] = kv.Value;

                snapshots = conversionResult.Snapshots;
            }

            // ── 3. 框架内置变量（最高优先级）──────────────────
            // 注意：驳回路径不会走到这里（已在 CompleteTaskAsync 提前分叉）
            variables["isApproved"] = true;
            variables["approvedBy"] = operatorId;
            variables["approvedTime"] = DateTime.UtcNow.ToString("O");

            if (!string.IsNullOrWhiteSpace(request.Comment))
                variables["approvalComment"] = request.Comment;

            _logger.LogDebug(
                "变量组装完成: TaskId={TaskId}, Keys=[{Keys}]",
                currentTask.Id, string.Join(", ", variables.Keys));

            return (variables, snapshots);
        }

        // ═══════════════════════════════════════════════════════════
        // 私有辅助方法
        // ═══════════════════════════════════════════════════════════

        private async Task<FlowableTask> ResolveTaskByIdAsync(
            string taskId,
            string expectedProcessInstanceId,
            string operatorId)
        {
            var task = await _taskService.GetTaskAsync(taskId);

            if (task == null)
                throw new BusinessException($"任务不存在: {taskId}");
            if (task.ProcessInstanceId != expectedProcessInstanceId)
                throw new BusinessException($"任务 [{taskId}] 不属于当前业务流程，禁止操作");

            var isAssignee = string.Equals(task.Assignee, operatorId,
                StringComparison.OrdinalIgnoreCase);

            if (!isAssignee)
            {
                var candidates = await _taskService.GetCandidateUsersAsync(taskId);
                var isCandidate = candidates.Any(c =>
                    string.Equals(c, operatorId, StringComparison.OrdinalIgnoreCase));
                if (!isCandidate)
                    throw new BusinessException(
                        $"操作人 [{operatorId}] 不是任务 [{taskId}] 的处理人，禁止操作");
            }

            return task;
        }

        private async Task<FlowableTask> FindTaskForOperatorAsync(
            string processInstanceId,
            string operatorId)
        {
            var activeTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                ProcessInstanceId = processInstanceId
            });

            if (!activeTasks.Any())
                throw new BusinessException("该流程下当前没有待办任务，流程可能已结束");

            var assigneeTask = activeTasks.FirstOrDefault(t =>
                string.Equals(t.Assignee, operatorId, StringComparison.OrdinalIgnoreCase));
            if (assigneeTask != null) return assigneeTask;

            var candidateChecks = activeTasks.Select(async task =>
            {
                var candidates = await _taskService.GetCandidateUsersAsync(task.Id);
                return candidates.Any(c =>
                    string.Equals(c, operatorId, StringComparison.OrdinalIgnoreCase))
                    ? task : null;
            });

            var candidateResults = await Task.WhenAll(candidateChecks);
            var matchedTasks = candidateResults.Where(t => t != null).ToList();

            if (!matchedTasks.Any())
                throw new BusinessException(
                    $"操作人 [{operatorId}] 在此流程下没有待办任务");

            if (matchedTasks.Count > 1)
            {
                _logger.LogError(
                    "操作人 [{OperatorId}] 在流程 [{ProcessInstanceId}] 下找到 {Count} 个候选任务，" +
                    "取第一个处理。建议并行场景下前端传入 taskId 明确指定",
                    operatorId, processInstanceId, matchedTasks.Count);
                throw new BusinessException(
                       $"操作人 [{operatorId}] 在流程 [{processInstanceId}] 下找到 {matchedTasks.Count} 个候选任务，" +
                    "取第一个处理。并行场景下前端传入 taskId 必须明确指定");
            }


            return matchedTasks.First();
        }

        private async Task WriteReassignAuditRecordSafeAsync(
            ProcessMetadataDocument metadata,
            FlowableTask task,
            ReassignTaskRequest request)
        {
            try
            {
                string nodeSemantic = null;
                string pageCode = null;
                try
                {
                    var semanticMap = await _slotConfigProvider
                        .GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey);
                    semanticMap.TryGetValue(task.TaskDefinitionKey, out var nodeInfo);
                    nodeSemantic = nodeInfo?.NodeSemantic;
                    pageCode     = nodeInfo?.PageCode;
                }
                catch { }

                var operatorId = ResolveOperatorId(request.OperatorId);

                var auditRecord = new ProcessAuditRecord
                {
                    Id                = Guid.NewGuid().ToString(),
                    ProcessInstanceId = metadata.ProcessInstanceId,
                    BusinessId        = metadata.BusinessId,
                    BusinessType      = metadata.BusinessType,
                    TaskId            = task.Id,
                    TaskDefinitionKey = task.TaskDefinitionKey,
                    NodeSemantic      = nodeSemantic,
                    PageCode          = pageCode,
                    Action            = "reassign",
                    OperatorId        = operatorId,
                    Comment           = string.IsNullOrWhiteSpace(request.Reason)
                                        ? $"转派给 {string.Join(",", request.NewAssignees)}"
                                        : $"{request.Reason}（转派给 {string.Join(",", request.NewAssignees)}）",
                    OperatedAt        = DateTime.UtcNow,
                    SlotSelections    = new List<SlotSelectionRecord>()
                };

                await _esService.IndexAuditRecordAsync(auditRecord);

                _logger.LogInformation(
                    "转派审计记录写入成功: TaskId={TaskId}, NewAssignees=[{Assignees}]",
                    task.Id, string.Join(",", request.NewAssignees));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "转派审计记录写入失败（不影响转派结果）: TaskId={TaskId}, BusinessId={BusinessId}",
                    task.Id, metadata.BusinessId);
            }
        }

        private async Task WriteAuditRecordSafeAsync(
            ProcessMetadataDocument metadata,
            FlowableTask task,
            CompleteTaskRequest request,
            string operatorId,
            List<SlotSelectionSnapshot> slotSnapshots,
            string rejectTargetNodeKey = null)
        {
            try
            {
                string nodeSemantic = null;
                string pageCode = null;
                List<SlotDefinition> currentSlotDefs = new List<SlotDefinition>();

                try
                {
                    var semanticMap = await _slotConfigProvider
                        .GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey);
                    semanticMap.TryGetValue(task.TaskDefinitionKey, out var nodeInfo);
                    nodeSemantic = nodeInfo?.NodeSemantic;
                    pageCode     = nodeInfo?.PageCode;
                    currentSlotDefs = nodeInfo?.Slots ?? new List<SlotDefinition>();
                }
                catch { }

                // ── 推荐范围越界审计（不拦截，只记录）────────────────
                var (hasOutOfRange, recSnapshot, restrictSnapshot) = EvaluateRecommendedRange(
                    request.NextSlotSelections,
                    currentSlotDefs,
                    metadata.RecommendedAssigneesSnapshot);

                var auditRecord = new ProcessAuditRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    ProcessInstanceId = metadata.ProcessInstanceId,
                    BusinessId = metadata.BusinessId,
                    BusinessType = metadata.BusinessType,
                    TaskId = task.Id,
                    TaskDefinitionKey = task.TaskDefinitionKey,
                    NodeSemantic = nodeSemantic,
                    PageCode = pageCode,
                    Action = request.Action == ApprovalAction.Approve
                                        ? "approve" : "reject",
                    OperatorId = operatorId,
                    Comment = request.Comment,
                    RejectReason = request.RejectReason,
                    RejectCode = request.RejectCode,
                    RejectTargetNodeKey = rejectTargetNodeKey,
                    OperatedAt = DateTime.UtcNow,
                    SlotSelections = slotSnapshots.Select(s => new SlotSelectionRecord
                    {
                        SlotKey = s.SlotKey,
                        Label = s.Label,
                        Users = s.Users
                    }).ToList(),
                    // ── 推荐范围审计字段 ─────────────────────────────
                    HasOutOfRecommendedRange      = hasOutOfRange,
                    RecommendedUsersSnapshot      = recSnapshot,
                    RestrictToRecommendedSnapshot = restrictSnapshot
                };

                await _esService.IndexAuditRecordAsync(auditRecord);

                _logger.LogInformation(
                    "审计记录写入成功: TaskId={TaskId}, Action={Action}",
                    task.Id, auditRecord.Action);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "审计记录写入失败（不影响流程）: TaskId={TaskId}, BusinessId={BusinessId}",
                    task.Id, metadata.BusinessId);
            }
        }

        /// <summary>
        /// 评估本次提交是否有人员越出推荐范围
        ///
        /// 判定规则：
        ///   - 只对 RestrictToRecommended = true 的 slot 做越界判定
        ///   - RestrictToRecommended = false 的 slot 直接跳过（不适用）
        ///   - 无推荐人快照时整体返回 null（不适用）
        ///
        /// 流程中心不拦截越界提交，只记录审计标记
        /// 日志关键字 [RECOMMEND_RANGE_EXCEEDED] 用于运维告警
        /// </summary>
        private (bool? hasOutOfRange,
                 Dictionary<string, List<string>> recommendedSnapshot,
                 Dictionary<string, bool> restrictSnapshot)
            EvaluateRecommendedRange(
                List<SlotSelection> nextSlotSelections,
                List<SlotDefinition> slotDefs,
                Dictionary<string, List<string>> recommendedSnapshot)
        {
            if (recommendedSnapshot == null || !recommendedSnapshot.Any())
                return (null, new Dictionary<string, List<string>>(), new Dictionary<string, bool>());

            if (slotDefs == null || !slotDefs.Any())
                return (null, new Dictionary<string, List<string>>(), new Dictionary<string, bool>());

            var restrictSnapshot = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var slotRecommendedSnapshot = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var slot in slotDefs)
            {
                if (string.IsNullOrWhiteSpace(slot.RoleKey))
                    throw new BusinessException(
                        $"Slot [{slot.Label}]（{slot.SlotKey}）roleKey 不能为空",
                        "SLOT_ROLE_KEY_REQUIRED");

                restrictSnapshot[slot.SlotKey] = slot.RestrictToRecommended;

                if (recommendedSnapshot.TryGetValue(slot.RoleKey, out var recommended)
                    && recommended?.Any() == true)
                {
                    slotRecommendedSnapshot[slot.SlotKey] = recommended;
                }
            }

            if (!slotRecommendedSnapshot.Any())
                return (null, slotRecommendedSnapshot, restrictSnapshot);

            // 只检查 RestrictToRecommended = true 的 slot
            var restrictedSlots = slotDefs
                .Where(d => d.RestrictToRecommended
                            && slotRecommendedSnapshot.ContainsKey(d.SlotKey))
                .ToList();

            // 无受限 slot → 不适用（null）
            if (!restrictedSlots.Any())
                return (null, slotRecommendedSnapshot, restrictSnapshot);

            // NextSlotSelections 按 slotKey 建立查找字典
            var selectionDict = (nextSlotSelections ?? new List<SlotSelection>())
                .GroupBy(s => s.SlotKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            bool hasOutOfRange = false;

            foreach (var slot in restrictedSlots)
            {
                if (!selectionDict.TryGetValue(slot.SlotKey, out var selection)) continue;
                if (selection.Users == null || !selection.Users.Any()) continue;
                if (!slotRecommendedSnapshot.TryGetValue(slot.SlotKey, out var recommended)) continue;

                var outOfRangeUsers = selection.Users
                    .Where(u => !recommended.Contains(u, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (outOfRangeUsers.Any())
                {
                    hasOutOfRange = true;
                    _logger.LogWarning(
                        "[RECOMMEND_RANGE_EXCEEDED] SlotKey={SlotKey} 提交了推荐范围外人员。" +
                        "OutOfRange=[{OutOfRange}], Recommended=[{Recommended}]",
                        slot.SlotKey,
                        string.Join(",", outOfRangeUsers),
                        string.Join(",", recommended));
                }
            }

            return (hasOutOfRange, slotRecommendedSnapshot, restrictSnapshot);
        }


        private string ResolveOperatorId(string requestEmployeeId)
        {
            var operatorId = !string.IsNullOrWhiteSpace(requestEmployeeId)
                ? requestEmployeeId
                : _currentUser.UserId;

            if (string.IsNullOrWhiteSpace(operatorId))
                throw new BusinessException(
                    "无法确定操作人，请传入 employeeId 或在 Header 中携带 X-User-Id");

            return operatorId;
        }
    }
}
