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

        public TaskExecutionAppService(
            IFlowableRuntimeService runtimeService,
            IFlowableTaskService taskService,
            IElasticSearchService esService,
            IProcessSlotConfigProvider slotConfigProvider,
            SlotVariableConverter slotConverter,
            ICurrentUser currentUser,
            ILogger<TaskExecutionAppService> logger)
        {
            _runtimeService = runtimeService;
            _taskService = taskService;
            _esService = esService;
            _slotConfigProvider = slotConfigProvider;
            _slotConverter = slotConverter;
            _currentUser = currentUser;
            _logger = logger;
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

            // ── Step 5 + 6: 组装变量 + 一次 CompleteAsync ─────────
            var (variables, slotSnapshots) = await BuildCompletionVariablesAsync(
                request, myTask, metadata, operatorId);

            await _taskService.CompleteAsync(myTask.Id, variables);

            _logger.LogInformation(
                "任务完成: TaskId={TaskId}, Action={Action}, 注入变量数={VarCount}",
                myTask.Id, request.Action, variables.Count);

            // ── Step 7: 写审计记录（失败不影响主流程）────────────
            await WriteAuditRecordSafeAsync(
                metadata, myTask, request, operatorId, slotSnapshots);

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

                result.Add(new PendingTaskDto
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    BusinessId = meta.BusinessId,
                    BusinessType = meta.BusinessType,
                    NodeSemantic = nodeInfo?.NodeSemantic,
                    PageCode = nodeInfo?.PageCode,
                    // RequiredSlots 已移除：Slot 是流程中心内部契约，不对前端暴露
                    // 前端通过 pageCode → COMPONENT_REGISTRY 找到表单组件，
                    // 表单组件自己知道要选哪些人
                    IsAfterConvergencePoint = nodeInfo?.IsConvergencePoint ?? false,
                    CreateTime = task.CreateTime,
                    Priority = task.Priority
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

            // 补强3：查所有活动任务，检查多实例并收集取消列表
            var allActiveTasks = await _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                ProcessInstanceId = metadata.ProcessInstanceId
            });

            var multiInstanceKeys = allActiveTasks
                .GroupBy(t => t.TaskDefinitionKey)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (multiInstanceKeys.Any())
                throw new BusinessException(
                    $"当前流程存在多实例节点 [{string.Join("、", multiInstanceKeys)}]，禁止驳回跳转",
                    "REJECT_MULTI_INSTANCE_FORBIDDEN");

            var cancelActivityIds = allActiveTasks
                .Select(t => t.TaskDefinitionKey)
                .Distinct()
                .ToList();

            if (cancelActivityIds.Count > 1)
                _logger.LogInformation(
                    "并行场景驳回，取消 {Count} 个节点: [{Nodes}]",
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
                new List<SlotSelectionSnapshot>());

            return new CompleteTaskResponse
            {
                Success = true,
                Message = $"已驳回（{matchedOption.Label}），流程已退回至【{targetNode.NodeSemantic}】"
            };
        }

        // ═══════════════════════════════════════════════════════════
        // BuildCompletionVariablesAsync（仅通过路径使用）
        // ═══════════════════════════════════════════════════════════

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
                _logger.LogWarning(
                    "操作人 [{OperatorId}] 在流程 [{ProcessInstanceId}] 下找到 {Count} 个候选任务，" +
                    "取第一个处理。建议并行场景下前端传入 taskId 明确指定",
                    operatorId, processInstanceId, matchedTasks.Count);

            return matchedTasks.First();
        }

        private async Task WriteAuditRecordSafeAsync(
            ProcessMetadataDocument metadata,
            FlowableTask task,
            CompleteTaskRequest request,
            string operatorId,
            List<SlotSelectionSnapshot> slotSnapshots)
        {
            try
            {
                string nodeSemantic = null;
                try
                {
                    var semanticMap = await _slotConfigProvider
                        .GetNodeSemanticMapAsync(metadata.ProcessDefinitionKey);
                    semanticMap.TryGetValue(task.TaskDefinitionKey, out var nodeInfo);
                    nodeSemantic = nodeInfo?.NodeSemantic;
                }
                catch { }

                var auditRecord = new ProcessAuditRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    ProcessInstanceId = metadata.ProcessInstanceId,
                    BusinessId = metadata.BusinessId,
                    BusinessType = metadata.BusinessType,
                    TaskId = task.Id,
                    TaskDefinitionKey = task.TaskDefinitionKey,
                    NodeSemantic = nodeSemantic,
                    Action = request.Action == ApprovalAction.Approve
                                        ? "approve" : "reject",
                    OperatorId = operatorId,
                    Comment = request.Comment,
                    RejectReason = request.RejectReason,
                    RejectCode = request.RejectCode,
                    OperatedAt = DateTime.UtcNow,
                    SlotSelections = slotSnapshots.Select(s => new SlotSelectionRecord
                    {
                        SlotKey = s.SlotKey,
                        Label = s.Label,
                        Users = s.Users
                    }).ToList()
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