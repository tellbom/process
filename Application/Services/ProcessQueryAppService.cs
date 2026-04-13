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
    /// 流程查询服务（只读）
    ///
    /// 职责：
    ///   ✔ 查询流程进度（当前节点 + 历史审批记录 + 基本状态）
    ///   ✔ 查询审批历史
    ///   ✔ 查询流程列表（分页）
    ///   ✔ 按 businessId 查单条流程状态
    ///
    /// 数据来源分离原则：
    ///   status / createdBy / createdTime / completedTime
    ///     → ES ProcessMetadataDocument（流程中心元数据）
    ///   当前活动节点 / assignee / candidateUsers
    ///     → Flowable TaskService 实时查询（执行态真相，不缓存）
    ///   历史审批记录 / 操作人 / 审批意见
    ///     → ES ProcessAuditRecord（流程中心写入的审计数据）
    ///
    /// 此服务严格只读，不调用任何写入操作
    /// </summary>
    public class ProcessQueryAppService
    {
        private readonly IFlowableTaskService _taskService;
        private readonly IElasticSearchService _esService;
        private readonly IProcessSlotConfigProvider _slotConfigProvider;
        private readonly ILogger<ProcessQueryAppService> _logger;

        public ProcessQueryAppService(
            IFlowableTaskService taskService,
            IElasticSearchService esService,
            IProcessSlotConfigProvider slotConfigProvider,
            ILogger<ProcessQueryAppService> logger)
        {
            _taskService         = taskService;
            _esService           = esService;
            _slotConfigProvider  = slotConfigProvider;
            _logger              = logger;
        }

        // ═══════════════════════════════════════════════════════════
        // GetProcessProgressAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 查询流程进度
        ///
        /// 返回：
        ///   - 流程基本信息（来自 ES）
        ///   - 当前活动节点列表（来自 Flowable，实时）
        ///   - 历史审批记录（来自 ES ProcessAuditRecord）
        ///
        /// 流程已结束时 currentNodes 为空列表，不报错
        /// </summary>
        public async Task<ProcessProgressDto> GetProcessProgressAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId))
                throw new BusinessException("businessId 不能为空");

            _logger.LogInformation("查询流程进度: BusinessId={BusinessId}", businessId);

            // ── 查 ES 元数据 ───────────────────────────────────────
            var metadata = await GetMetadataByBusinessIdAsync(businessId);

            // ── 并行查：当前任务（Flowable）+ 审计记录（ES）────────
            var currentTasksTask = _taskService.QueryTasksAsync(new FlowableTaskQuery
            {
                ProcessInstanceId = metadata.ProcessInstanceId
            });

            var auditRecordsTask = _esService.QueryAuditRecordsByBusinessIdAsync(businessId);

            await Task.WhenAll(currentTasksTask, auditRecordsTask);

            var currentTasks  = currentTasksTask.Result;
            var auditRecords  = auditRecordsTask.Result;

            // ── 当前节点：补充 nodeSemantic / pageCode / candidateUsers ──
            var currentNodes = await BuildCurrentNodesAsync(
                currentTasks, metadata.ProcessDefinitionKey);

            // ── 审批历史：映射为 DTO ───────────────────────────────
            var auditHistory = auditRecords.Select(MapAuditRecord).ToList();

            return new ProcessProgressDto
            {
                BusinessId           = metadata.BusinessId,
                ProcessInstanceId    = metadata.ProcessInstanceId,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                BusinessType         = metadata.BusinessType,
                Status               = metadata.Status,
                CreatedBy            = metadata.CreatedBy,
                CreatedTime          = metadata.CreatedTime,
                CompletedTime        = metadata.CompletedTime,
                CurrentNodes         = currentNodes,
                AuditHistory         = auditHistory
            };
        }

        // ═══════════════════════════════════════════════════════════
        // GetAuditHistoryAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 查询审批历史（轻量，不含当前节点信息）
        /// 适用于只需要历史记录、不需要当前节点的场景
        /// </summary>
        public async Task<List<AuditRecordDto>> GetAuditHistoryAsync(
            AuditHistoryRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.BusinessId))
                throw new BusinessException("businessId 不能为空");

            var records = await _esService.QueryAuditRecordsByBusinessIdAsync(
                request.BusinessId);

            return records.Select(MapAuditRecord).ToList();
        }

        // ═══════════════════════════════════════════════════════════
        // GetProcessListAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 分页查询流程列表
        /// 轻量列表，不含当前节点和审批历史
        /// </summary>
        public async Task<ProcessListResult> GetProcessListAsync(
            ProcessListRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // PageSize 上限保护
            if (request.PageSize > 100) request.PageSize = 100;
            if (request.PageSize < 1)  request.PageSize = 20;
            if (request.PageIndex < 1) request.PageIndex = 1;

            var (items, total) = await _esService.QueryProcessListAsync(request);

            return new ProcessListResult
            {
                Items = items.Select(m => new ProcessListItemDto
                {
                    ProcessInstanceId    = m.ProcessInstanceId,
                    BusinessId           = m.BusinessId,
                    BusinessType         = m.BusinessType,
                    ProcessDefinitionKey = m.ProcessDefinitionKey,
                    Status               = m.Status,
                    CreatedBy            = m.CreatedBy,
                    CreatedTime          = m.CreatedTime,
                    CompletedTime        = m.CompletedTime
                }).ToList(),
                Total     = total,
                PageIndex = request.PageIndex,
                PageSize  = request.PageSize
            };
        }

        // ═══════════════════════════════════════════════════════════
        // GetProcessStatusByBusinessIdAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 按 businessId 查单条流程状态（轻量，仅基本信息）
        /// 业务系统轮询流程状态时使用此接口，避免每次都拉全量进度
        /// </summary>
        public async Task<ProcessListItemDto> GetProcessStatusByBusinessIdAsync(
            string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId))
                throw new BusinessException("businessId 不能为空");

            // 优先查 running 状态（GetProcessMetadataByBusinessIdAsync 已过滤 running）
            var metadata = await _esService.GetProcessMetadataByBusinessIdAsync(businessId);

            if (metadata == null)
            {
                // 未找到 running 状态的流程，尝试查所有状态
                // 通过列表查询，取最近一条
                var (items, _) = await _esService.QueryProcessListAsync(
                    new ProcessListRequest
                    {
                        PageIndex = 1,
                        PageSize  = 1
                    });

                // 按 businessId 精确匹配
                metadata = items.FirstOrDefault(m =>
                    string.Equals(m.BusinessId, businessId,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (metadata == null)
                throw new BusinessException($"未找到 businessId 对应的流程: {businessId}");

            return new ProcessListItemDto
            {
                ProcessInstanceId    = metadata.ProcessInstanceId,
                BusinessId           = metadata.BusinessId,
                BusinessType         = metadata.BusinessType,
                ProcessDefinitionKey = metadata.ProcessDefinitionKey,
                Status               = metadata.Status,
                CreatedBy            = metadata.CreatedBy,
                CreatedTime          = metadata.CreatedTime,
                CompletedTime        = metadata.CompletedTime
            };
        }

        // ═══════════════════════════════════════════════════════════
        // 私有辅助方法
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 查流程元数据，流程处于任意状态都能查到
        /// GetProcessMetadataByBusinessIdAsync 只查 running，此处兼容所有状态
        /// </summary>
        private async Task<ProcessMetadataDocument> GetMetadataByBusinessIdAsync(
            string businessId)
        {
            // 先查 running（最常见场景）
            var metadata = await _esService.GetProcessMetadataByBusinessIdAsync(businessId);
            if (metadata != null) return metadata;

            // 未找到 running → 流程可能已结束，通过列表查询找到
            var (items, _) = await _esService.QueryProcessListAsync(
                new ProcessListRequest { PageIndex = 1, PageSize = 1 });

            metadata = items.FirstOrDefault(m =>
                string.Equals(m.BusinessId, businessId, StringComparison.OrdinalIgnoreCase));

            if (metadata == null)
                throw new BusinessException($"未找到 businessId 对应的流程: {businessId}");

            return metadata;
        }

        /// <summary>
        /// 构建当前节点列表
        /// 对每个活动任务：
        ///   - 从 ES nodeSemanticMap 补充 nodeSemantic / pageCode
        ///   - 并行查 Flowable 获取 candidateUsers
        /// </summary>
        private async Task<List<CurrentNodeDto>> BuildCurrentNodesAsync(
            List<FlowableTask> activeTasks,
            string processDefinitionKey)
        {
            if (!activeTasks.Any())
                return new List<CurrentNodeDto>();

            // 查节点语义（一次查全，缓存用）
            Dictionary<string, NodeSemanticInfo> semanticMap;
            try
            {
                semanticMap = await _slotConfigProvider
                    .GetNodeSemanticMapAsync(processDefinitionKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "查询节点语义映射失败，当前节点将缺少语义信息: {Key}",
                    processDefinitionKey);
                semanticMap = new Dictionary<string, NodeSemanticInfo>();
            }

            // 并行查各任务的候选人列表（减少串行 RTT）
            var candidateTasks = activeTasks.Select(async task =>
            {
                List<string> candidates;
                try
                {
                    candidates = await _taskService.GetCandidateUsersAsync(task.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "查询候选人失败: TaskId={TaskId}", task.Id);
                    candidates = new List<string>();
                }
                return (task, candidates);
            });

            var taskWithCandidates = await Task.WhenAll(candidateTasks);

            return taskWithCandidates.Select(tc =>
            {
                var (task, candidates) = tc;
                semanticMap.TryGetValue(task.TaskDefinitionKey, out var nodeInfo);

                return new CurrentNodeDto
                {
                    TaskId         = task.Id,
                    NodeId         = task.TaskDefinitionKey,
                    NodeName       = task.Name,
                    NodeSemantic   = nodeInfo?.NodeSemantic,
                    PageCode       = nodeInfo?.PageCode,
                    Assignee       = task.Assignee,
                    CandidateUsers = candidates,
                    CreateTime     = task.CreateTime
                };
            }).ToList();
        }

        /// <summary>
        /// 将 ProcessAuditRecord 映射为 AuditRecordDto
        /// </summary>
        private static AuditRecordDto MapAuditRecord(ProcessAuditRecord record)
        {
            return new AuditRecordDto
            {
                TaskDefinitionKey = record.TaskDefinitionKey,
                NodeSemantic      = record.NodeSemantic,
                Action            = record.Action,
                OperatorId        = record.OperatorId,
                Comment           = record.Comment,
                RejectReason      = record.RejectReason,
                OperatedAt        = record.OperatedAt,
                SlotSelections    = record.SlotSelections?.Select(s =>
                    new SlotSelectionRecordDto
                    {
                        SlotKey = s.SlotKey,
                        Label   = s.Label,
                        Users   = s.Users
                    }).ToList() ?? new List<SlotSelectionRecordDto>()
            };
        }
    }
}
