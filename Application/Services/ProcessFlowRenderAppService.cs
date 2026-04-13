using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    /// 流程图渲染数据服务
    ///
    /// 职责：
    ///   将 BPMN XML 结构 + Flowable 执行态 + ES 审计记录
    ///   组装成 Flowgraph.vue 所需的 ProcessFlowRenderDto
    ///
    /// 设计原则：
    ///   - 严格只读，不写入任何数据
    ///   - 节点状态染色规则由本服务决定，前端只消费状态值
    ///   - 坐标从 BPMN DI 解析，无 DI 时返回 null（前端走 dagre 自动布局）
    ///   - 这是展示模块，不驱动任何流程行为
    /// </summary>
    public class ProcessFlowRenderAppService
    {
        private static readonly XNamespace BpmnNs    = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        private static readonly XNamespace BpmnDiNs  = "http://www.omg.org/spec/BPMN/20100524/DI";
        private static readonly XNamespace DcNs      = "http://www.omg.org/spec/DD/20100524/DC";
        private static readonly XNamespace DiNs      = "http://www.omg.org/spec/DD/20100524/DI";
        private static readonly XNamespace FlowableNs = "http://flowable.org/bpmn";

        private readonly IElasticSearchService _esService;
        private readonly IFlowableTaskService _taskService;
        private readonly IFlowableHistoryService _historyService;
        private readonly IFlowableRepositoryService _repositoryService;
        private readonly IProcessSlotConfigProvider _slotConfigProvider;
        private readonly ILogger<ProcessFlowRenderAppService> _logger;

        public ProcessFlowRenderAppService(
            IElasticSearchService esService,
            IFlowableTaskService taskService,
            IFlowableHistoryService historyService,
            IFlowableRepositoryService repositoryService,
            IProcessSlotConfigProvider slotConfigProvider,
            ILogger<ProcessFlowRenderAppService> logger)
        {
            _esService          = esService;
            _taskService        = taskService;
            _historyService     = historyService;
            _repositoryService  = repositoryService;
            _slotConfigProvider = slotConfigProvider;
            _logger             = logger;
        }

        // ═══════════════════════════════════════════════════════════
        // GetFlowRenderAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 获取流程图渲染数据
        /// </summary>
        public async Task<ProcessFlowRenderDto> GetFlowRenderAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId))
                throw new BusinessException("businessId 不能为空");

            // ── 查流程元数据（兼容所有状态）──────────────────────
            var metadata = await GetMetadataAnyStatusAsync(businessId);

            // ── 并行获取所需数据 ───────────────────────────────────
            var activeTasksTask = _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                ProcessInstanceId = metadata.ProcessInstanceId
            });

            var auditRecordsTask = _esService.QueryAuditRecordsByBusinessIdAsync(businessId);

            var historicTasksTask = _historyService.QueryHistoricTasksAsync(
                new FlowableHistoricTaskQuery
                {
                    ProcessInstanceId = metadata.ProcessInstanceId,
                    Finished = true
                });

            await Task.WhenAll(activeTasksTask, auditRecordsTask, historicTasksTask);

            var activeTasks   = activeTasksTask.Result;
            var auditRecords  = auditRecordsTask.Result;
            var historicTasks = historicTasksTask.Result;

            // ── 获取 BPMN XML ──────────────────────────────────────
            var bpmnXml = await GetBpmnXmlAsync(metadata.ProcessDefinitionKey);
            if (string.IsNullOrWhiteSpace(bpmnXml))
            {
                _logger.LogWarning(
                    "未找到 BPMN XML: ProcessDefinitionKey={Key}，将返回空节点列表",
                    metadata.ProcessDefinitionKey);
                bpmnXml = null;
            }

            // ── 构建节点和边 ───────────────────────────────────────
            List<FlowNodeDto> nodes = new();
            List<FlowEdgeDto> edges = new();
            List<string> walkedNodeIds = new();

            if (bpmnXml != null)
            {
                var doc = XDocument.Parse(bpmnXml);
                var completedNodeIds = historicTasks
                    .Where(h => h.EndTime.HasValue)
                    .Select(h => h.TaskDefinitionKey)
                    .ToHashSet();
                var activeNodeIds = activeTasks
                    .Select(t => t.TaskDefinitionKey)
                    .ToHashSet();

                nodes = BuildNodes(doc, completedNodeIds, activeNodeIds,
                    activeTasks, historicTasks, auditRecords);
                edges = BuildEdges(doc, completedNodeIds, activeNodeIds, auditRecords);

                walkedNodeIds = nodes
                    .Where(n => n.State == "completed" || n.State == "active"
                                || n.State == "rejected")
                    .Select(n => n.Id)
                    .ToList();
            }

            // ── 构建 activeTasks 渲染数据 ──────────────────────────
            var activeTaskRenders = await BuildActiveTaskRendersAsync(activeTasks);

            // ── 构建 completedRecords 渲染数据 ─────────────────────
            var completedRecords = BuildCompletedRecords(auditRecords, historicTasks);

            // ── 构建 rejectHistory ─────────────────────────────────
            var rejectHistory = BuildRejectHistory(auditRecords, nodes);
            var hasRejectHistory = rejectHistory.Any();

            return new ProcessFlowRenderDto
            {
                BusinessId           = metadata.BusinessId,
                ProcessInstanceId    = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType         = metadata.BusinessType,
                Status               = metadata.Status,
                CreatedBy            = metadata.CreatedBy,
                CreatedTime          = metadata.CreatedTime,
                CompletedTime        = metadata.CompletedTime,
                HasRejectHistory     = hasRejectHistory,
                WalkedNodeIds        = walkedNodeIds,
                Nodes                = nodes,
                Edges                = edges,
                ActiveTasks          = activeTaskRenders,
                CompletedRecords     = completedRecords,
                RejectHistory        = rejectHistory
            };
        }

        // ═══════════════════════════════════════════════════════════
        // 节点构建
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 从 BPMN XML 解析所有节点，结合执行状态染色
        ///
        /// 节点状态染色规则：
        ///   active    = Flowable 当前有活动任务
        ///   completed = 历史任务中已结束（且无驳回记录）
        ///   rejected  = 审计记录中有 reject，该节点被驳回过
        ///   pending   = 尚未流转到
        /// </summary>
        private List<FlowNodeDto> BuildNodes(
            XDocument doc,
            HashSet<string> completedNodeIds,
            HashSet<string> activeNodeIds,
            List<FlowableTask> activeTasks,
            List<FlowableHistoricTask> historicTasks,
            List<ProcessAuditRecord> auditRecords)
        {
            var result = new List<FlowNodeDto>();

            // 解析 DI 坐标（BPMNShape → bounds）
            var diCoords = ParseDiCoords(doc);

            // 审计记录中驳回过的节点
            var rejectedNodeIds = auditRecords
                .Where(r => r.Action == "reject")
                .Select(r => r.TaskDefinitionKey)
                .ToHashSet();

            // 历史任务中各节点的 assignee
            var historicAssigneeMap = historicTasks
                .GroupBy(h => h.TaskDefinitionKey)
                .ToDictionary(g => g.Key,
                    g => g.Select(h => h.Assignee)
                          .Where(a => !string.IsNullOrWhiteSpace(a))
                          .Distinct()
                          .ToList());

            // 当前活动任务的 assignee
            var activeAssigneeMap = activeTasks
                .GroupBy(t => t.TaskDefinitionKey)
                .ToDictionary(g => g.Key,
                    g => g.Select(t => t.Assignee)
                          .Where(a => !string.IsNullOrWhiteSpace(a))
                          .Distinct()
                          .ToList());

            // 历史任务中各节点最新完成时间
            var completedAtMap = historicTasks
                .Where(h => h.EndTime.HasValue)
                .GroupBy(h => h.TaskDefinitionKey)
                .ToDictionary(g => g.Key,
                    g => g.Max(h => h.EndTime));

            // 遍历所有 BPMN 元素
            var process = doc.Descendants(BpmnNs + "process").FirstOrDefault();
            if (process == null) return result;

            foreach (var element in process.Elements())
            {
                var id       = element.Attribute("id")?.Value;
                var name     = element.Attribute("name")?.Value ?? "";
                var nodeType = element.Name.LocalName;

                if (string.IsNullOrWhiteSpace(id)) continue;

                // 只处理可渲染的节点类型
                if (!IsRenderableNode(nodeType)) continue;

                var state    = DetermineNodeState(id, nodeType,
                    completedNodeIds, activeNodeIds, rejectedNodeIds);

                // Assignees
                var assignees = new List<string>();
                if (activeAssigneeMap.TryGetValue(id, out var aa)) assignees = aa;
                else if (historicAssigneeMap.TryGetValue(id, out var ha)) assignees = ha;

                // 多实例标记
                var isMultiInstance =
                    element.Element(BpmnNs + "multiInstanceLoopCharacteristics") != null;

                // DI 坐标
                diCoords.TryGetValue(id, out var coord);

                result.Add(new FlowNodeDto
                {
                    Id              = id,
                    Label           = name,
                    NodeType        = nodeType,
                    State           = state,
                    Assignees       = assignees,
                    CompletedAt     = completedAtMap.GetValueOrDefault(id),
                    IsMultiInstance = isMultiInstance,
                    X               = coord?.X,
                    Y               = coord?.Y,
                    Width           = coord?.Width,
                    Height          = coord?.Height
                });
            }

            return result;
        }

        /// <summary>
        /// 节点状态判断
        /// </summary>
        private string DetermineNodeState(
            string nodeId,
            string nodeType,
            HashSet<string> completedNodeIds,
            HashSet<string> activeNodeIds,
            HashSet<string> rejectedNodeIds)
        {
            // 非 userTask 节点（网关/事件）：走过即 completed
            if (nodeType != "userTask")
            {
                if (completedNodeIds.Contains(nodeId) || activeNodeIds.Contains(nodeId))
                    return "completed";
                return "pending";
            }

            // userTask 节点
            if (activeNodeIds.Contains(nodeId))   return "active";
            if (rejectedNodeIds.Contains(nodeId)) return "rejected";
            if (completedNodeIds.Contains(nodeId)) return "completed";
            return "pending";
        }

        // ═══════════════════════════════════════════════════════════
        // 边构建
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 从 BPMN XML 解析所有边，并根据节点走过情况染色
        ///
        /// 边状态染色规则：
        ///   walked   = source 节点已完成（包含驳回），边已走过
        ///   active   = source 节点 active，边正在流转
        ///   rejected = 驳回回退边（source 有驳回，target 是被回退的节点）
        ///   pending  = 其他
        /// </summary>
        private List<FlowEdgeDto> BuildEdges(
            XDocument doc,
            HashSet<string> completedNodeIds,
            HashSet<string> activeNodeIds,
            List<ProcessAuditRecord> auditRecords)
        {
            var result = new List<FlowEdgeDto>();

            // 驳回回退边：审计记录 action=reject 时，source=驳回节点，target=被回退节点
            // 通过查 Flowable 历史序列流来识别（简化处理：凡是 completed 节点连向 completed 节点
            // 且顺序反向的，标记为 rejected 边）
            // 当前实现：从审计记录推断驳回边
            var rejectPairs = auditRecords
                .Where(r => r.Action == "reject")
                .Select(r => r.TaskDefinitionKey)
                .ToHashSet();

            var process = doc.Descendants(BpmnNs + "process").FirstOrDefault();
            if (process == null) return result;

            foreach (var sf in process.Elements(BpmnNs + "sequenceFlow"))
            {
                var id        = sf.Attribute("id")?.Value    ?? Guid.NewGuid().ToString();
                var source    = sf.Attribute("sourceRef")?.Value;
                var target    = sf.Attribute("targetRef")?.Value;
                var branchLabel = GetFlowableField(sf, "branchLabel");

                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    continue;

                var state = DetermineEdgeState(
                    source, target, completedNodeIds, activeNodeIds, rejectPairs);

                result.Add(new FlowEdgeDto
                {
                    Id     = id,
                    Source = source,
                    Target = target,
                    State  = state,
                    Label  = branchLabel
                });
            }

            return result;
        }

        private string DetermineEdgeState(
            string source,
            string target,
            HashSet<string> completedNodeIds,
            HashSet<string> activeNodeIds,
            HashSet<string> rejectSourceNodeIds)
        {
            // 驳回边：source 节点有驳回记录，且 target 是已走过的节点（回退）
            if (rejectSourceNodeIds.Contains(source)
                && completedNodeIds.Contains(target))
                return "rejected";

            if (completedNodeIds.Contains(source)) return "walked";
            if (activeNodeIds.Contains(source))    return "active";
            return "pending";
        }

        // ═══════════════════════════════════════════════════════════
        // 活动任务渲染
        // ═══════════════════════════════════════════════════════════

        private async Task<List<ActiveTaskRenderDto>> BuildActiveTaskRendersAsync(
            List<FlowableTask> activeTasks)
        {
            var result = new List<ActiveTaskRenderDto>();

            // 并行查候选人
            var candidateChecks = activeTasks.Select(async task =>
            {
                List<string> candidates;
                try
                {
                    candidates = await _taskService.GetCandidateUsersAsync(task.Id);
                }
                catch
                {
                    candidates = new List<string>();
                }
                return (task, candidates);
            });

            var taskWithCandidates = await Task.WhenAll(candidateChecks);

            foreach (var (task, candidates) in taskWithCandidates)
            {
                var waitingSeconds = (long)(DateTime.UtcNow - task.CreateTime).TotalSeconds;
                result.Add(new ActiveTaskRenderDto
                {
                    TaskId         = task.Id,
                    NodeId         = task.TaskDefinitionKey,
                    NodeName       = task.Name,
                    Assignee       = task.Assignee,
                    CandidateUsers = candidates,
                    CreatedAt      = task.CreateTime,
                    WaitingSeconds = Math.Max(0, waitingSeconds)
                });
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // 已完成审批记录
        // ═══════════════════════════════════════════════════════════

        private List<CompletedRecordRenderDto> BuildCompletedRecords(
            List<ProcessAuditRecord> auditRecords,
            List<FlowableHistoricTask> historicTasks)
        {
            // 以审计记录为主，补充历史任务的时间信息
            var historicMap = historicTasks
                .GroupBy(h => h.TaskDefinitionKey)
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<CompletedRecordRenderDto>();
            int round = 1;
            string lastNodeId = null;

            foreach (var record in auditRecords.OrderBy(r => r.OperatedAt))
            {
                // 同一节点第二次出现（驳回后重走）轮次递增
                if (record.TaskDefinitionKey == lastNodeId)
                    round++;
                else
                    round = 1;
                lastNodeId = record.TaskDefinitionKey;

                // 从历史任务找时间
                DateTime startTime = record.OperatedAt;
                DateTime endTime   = record.OperatedAt;
                long duration      = 0;

                if (historicMap.TryGetValue(record.TaskDefinitionKey, out var hList))
                {
                    var matched = hList.FirstOrDefault(h =>
                        h.Assignee == record.OperatorId && h.EndTime.HasValue);
                    if (matched != null)
                    {
                        startTime = matched.StartTime;
                        endTime   = matched.EndTime!.Value;
                        duration  = matched.DurationInMillis.HasValue
                            ? matched.DurationInMillis.Value / 1000
                            : (long)(endTime - startTime).TotalSeconds;
                    }
                }

                // 推断 outcome
                string outcome;
                if (record.Action == "reject")
                    outcome = "rejected_return"; // 简化：驳回统一用 rejected_return
                else
                    outcome = "approved";

                result.Add(new CompletedRecordRenderDto
                {
                    TaskId          = record.TaskId,
                    NodeId          = record.TaskDefinitionKey,
                    NodeName        = record.NodeSemantic ?? record.TaskDefinitionKey,
                    OperatorId      = record.OperatorId,
                    StartTime       = startTime,
                    EndTime         = endTime,
                    DurationSeconds = duration,
                    Outcome         = outcome,
                    RejectReason    = record.RejectReason,
                    Comment         = record.Comment,
                    Round           = round
                });
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // 驳回轨迹
        // ═══════════════════════════════════════════════════════════

        private List<RejectHistoryRenderDto> BuildRejectHistory(
            List<ProcessAuditRecord> auditRecords,
            List<FlowNodeDto> nodes)
        {
            var nodeNameMap = nodes.ToDictionary(n => n.Id, n => n.Label);

            // 驳回记录：取 action=reject 的审计记录
            // targetNode 通过相邻记录推断（驳回后下一条记录的节点即为回退目标）
            var rejectRecords = auditRecords
                .Where(r => r.Action == "reject")
                .OrderBy(r => r.OperatedAt)
                .ToList();

            var result = new List<RejectHistoryRenderDto>();

            foreach (var rr in rejectRecords)
            {
                // 找驳回后的下一条审计记录，其节点即为回退目标
                var nextRecord = auditRecords
                    .Where(r => r.OperatedAt > rr.OperatedAt)
                    .OrderBy(r => r.OperatedAt)
                    .FirstOrDefault();

                var targetNodeId   = nextRecord?.TaskDefinitionKey ?? "";
                var targetNodeName = nodeNameMap.GetValueOrDefault(targetNodeId, targetNodeId);

                nodeNameMap.TryGetValue(rr.TaskDefinitionKey, out var rejectNodeName);

                result.Add(new RejectHistoryRenderDto
                {
                    RejectId       = rr.Id,
                    RejectBy       = rr.OperatorId,
                    RejectNodeId   = rr.TaskDefinitionKey,
                    RejectNodeName = rejectNodeName ?? rr.TaskDefinitionKey,
                    TargetNodeId   = targetNodeId,
                    TargetNodeName = targetNodeName,
                    RejectReason   = rr.RejectReason,
                    RejectTime     = rr.OperatedAt
                });
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // BPMN DI 坐标解析
        // ═══════════════════════════════════════════════════════════

        private record DiCoord(double X, double Y, double Width, double Height);

        /// <summary>
        /// 从 BPMN BPMNDiagram 段解析节点坐标
        /// 若 BPMN 无 DI 段（纯代码部署），返回空字典，前端走 dagre 自动布局
        /// </summary>
        private Dictionary<string, DiCoord> ParseDiCoords(XDocument doc)
        {
            var result = new Dictionary<string, DiCoord>();

            var shapes = doc.Descendants(BpmnDiNs + "BPMNShape");
            foreach (var shape in shapes)
            {
                var bpmnElement = shape.Attribute("bpmnElement")?.Value;
                if (string.IsNullOrWhiteSpace(bpmnElement)) continue;

                var bounds = shape.Element(DcNs + "Bounds");
                if (bounds == null) continue;

                if (double.TryParse(bounds.Attribute("x")?.Value,      out var x) &&
                    double.TryParse(bounds.Attribute("y")?.Value,      out var y) &&
                    double.TryParse(bounds.Attribute("width")?.Value,  out var w) &&
                    double.TryParse(bounds.Attribute("height")?.Value, out var h))
                {
                    result[bpmnElement] = new DiCoord(x, y, w, h);
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════════

        private static bool IsRenderableNode(string nodeType) => nodeType is
            "userTask" or "serviceTask" or
            "startEvent" or "endEvent" or
            "parallelGateway" or "exclusiveGateway" or "inclusiveGateway";

        private static string GetFlowableField(XElement element, string fieldName)
        {
            var ext = element.Element(BpmnNs + "extensionElements");
            if (ext == null) return null;
            return ext.Elements(FlowableNs + "field")
                .FirstOrDefault(f => f.Attribute("name")?.Value == fieldName)
                ?.Attribute("stringValue")?.Value;
        }

        private async Task<ProcessMetadataDocument> GetMetadataAnyStatusAsync(
            string businessId)
        {
            // 先查 running
            var meta = await _esService.GetProcessMetadataByBusinessIdAsync(businessId);
            if (meta != null) return meta;

            // 查已结束的
            var (items, _) = await _esService.QueryProcessListAsync(
                new ProcessListRequest { PageIndex = 1, PageSize = 1 });
            meta = items.FirstOrDefault(m =>
                string.Equals(m.BusinessId, businessId, StringComparison.OrdinalIgnoreCase));

            if (meta == null)
                throw new BusinessException($"未找到 businessId 对应的流程: {businessId}");

            return meta;
        }

        private async Task<string> GetBpmnXmlAsync(string processDefinitionKey)
        {
            try
            {
                // 通过 Flowable Repository API 获取最新 BPMN XML
                // Flowable REST: GET /repository/process-definitions/{id}/resourcedata
                // 这里简化为通过 key 查最新定义
                var definition = await _repositoryService
                    .GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);

                if (definition == null)
                {
                    _logger.LogWarning(
                        "未找到流程定义: {Key}", processDefinitionKey);
                    return null;
                }

                // TODO: IFlowableRepositoryService 需要补充 GetBpmnXmlAsync 方法
                // 当前先返回 null，前端走 dagre 布局
                // Phase 10 后续扩展：在 IFlowableRepositoryService 中增加
                // Task<string> GetBpmnXmlByDefinitionIdAsync(string processDefinitionId)
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "获取 BPMN XML 失败: {Key}，将返回 null", processDefinitionKey);
                return null;
            }
        }
    }
}
