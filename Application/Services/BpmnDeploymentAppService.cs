using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Application.Services
{
    public class BpmnDeploymentAppService
    {
        private static readonly XNamespace BpmnNs = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        private static readonly XNamespace FlowableNs = "http://flowable.org/bpmn";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IFlowableRepositoryService _repositoryService;
        private readonly IElasticSearchService _esService;
        private readonly ILogger<BpmnDeploymentAppService> _logger;

        public BpmnDeploymentAppService(
            IFlowableRepositoryService repositoryService,
            IElasticSearchService esService,
            ILogger<BpmnDeploymentAppService> logger)
        {
            _repositoryService = repositoryService;
            _esService = esService;
            _logger = logger;
        }

        // ═══════════════════════════════════════════════════════════
        // DeployAsync
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 部署 BPMN 文件并写入节点语义配置
        ///
        /// 执行顺序（防不一致）：
        ///   解析XML → 校验slotConfig → 部署Flowable → 写ES
        /// </summary>
        public async Task<BpmnDeploymentResponse> DeployAsync(
            IFormFile file,
            string slotConfigJson)
        {
            if (file == null || file.Length == 0)
                throw new BusinessException("BPMN 文件不能为空");

            var fileName = file.FileName;
            if (!fileName.EndsWith(".bpmn", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".bpmn20.xml", StringComparison.OrdinalIgnoreCase))
                throw new BusinessException("只支持 .bpmn 或 .bpmn20.xml 文件");

            string bpmnXml;
            using (var reader = new StreamReader(file.OpenReadStream()))
                bpmnXml = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(bpmnXml))
                throw new BusinessException("BPMN 文件内容为空");

            // Step 1: 解析 XML
            XDocument doc;
            try { doc = XDocument.Parse(bpmnXml); }
            catch (Exception ex)
            {
                throw new BusinessException($"BPMN XML 格式错误: {ex.Message}");
            }

            var processDefinitionKey = doc.Descendants(BpmnNs + "process")
                .FirstOrDefault()?.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(processDefinitionKey))
                throw new BusinessException("BPMN 中未找到 <process id>");

            var processDefinitionName = doc.Descendants(BpmnNs + "process")
                .FirstOrDefault()?.Attribute("name")?.Value;

            _logger.LogInformation(
                "开始部署 BPMN: {FileName}, Key={Key}", fileName, processDefinitionKey);

            // Step 2: 从 XML extensionElements 解析节点语义
            var nodeSemanticMap = ParseNodeSemantics(doc);

            // Step 3: 解析并校验 slotConfig
            var slotConfig = ParseSlotConfig(slotConfigJson);
            ValidateSlotConfig(slotConfig, processDefinitionKey);

            // Step 4: 合并 slotConfig 到节点语义
            MergeSlotConfig(nodeSemanticMap, slotConfig);

            // Step 5: 部署到 Flowable
            FlowableDeployment deployment;
            try
            {
                deployment = await _repositoryService.DeployBpmnAsync(fileName, bpmnXml);
            }
            catch (Exception ex)
            {
                throw new BusinessException($"BPMN 部署到 Flowable 失败: {ex.Message}");
            }

            var processDefinition = await _repositoryService
                .GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);

            // Step 6: 写入 ES
            await _esService.SaveNodeSemanticMapAsync(processDefinitionKey, nodeSemanticMap);

            _logger.LogInformation(
                "部署完成: Key={Key}, 节点数={Count}, DeploymentId={Id}",
                processDefinitionKey, nodeSemanticMap.Count, deployment.Id);

            return new BpmnDeploymentResponse
            {
                DeploymentId = deployment.Id,
                ProcessDefinitionKey = processDefinitionKey,
                ProcessDefinitionName = processDefinitionName,
                Version = processDefinition?.Version ?? 1,
                DeploymentTime = deployment.DeploymentTime,
                NodeSemanticCount = nodeSemanticMap.Count,
                Nodes = nodeSemanticMap.Values.Select(n => new NodeSemanticSummary
                {
                    TaskDefinitionKey = n.TaskDefinitionKey,
                    NodeSemantic = n.NodeSemantic,
                    PageCode = n.PageCode,
                    IsStarterNode = n.IsStarterNode,
                    IsConvergencePoint = n.IsConvergencePoint,
                    CanReject = n.CanReject,
                    IsRejectTarget = n.IsRejectTarget,
                    RejectCode = n.RejectCode,
                    SlotCount = n.Slots?.Count ?? 0,
                    RejectOptionCount = n.RejectOptions?.Count ?? 0
                }).ToList()
            };
        }

        // ═══════════════════════════════════════════════════════════
        // GetProcessDefinitionNodesAsync
        // ═══════════════════════════════════════════════════════════

        public async Task<List<ProcessDefinitionNodeDto>> GetProcessDefinitionNodesAsync(
            string processDefinitionKey)
        {
            if (string.IsNullOrWhiteSpace(processDefinitionKey))
                throw new BusinessException("processDefinitionKey 不能为空");

            var map = await _esService.GetNodeSemanticMapAsync(processDefinitionKey);

            return map.Values.Select(n => new ProcessDefinitionNodeDto
            {
                TaskDefinitionKey = n.TaskDefinitionKey,
                NodeSemantic = n.NodeSemantic,
                PageCode = n.PageCode,
                IsStarterNode = n.IsStarterNode,
                IsConvergencePoint = n.IsConvergencePoint,
                CanReject = n.CanReject,
                RejectOptions = n.RejectOptions ?? new List<RejectOption>(),
                IsRejectTarget = n.IsRejectTarget,
                RejectCode = n.RejectCode,
                Slots = n.Slots ?? new List<SlotDefinition>()
            }).ToList();
        }

        // ═══════════════════════════════════════════════════════════
        // DeleteDeploymentAsync（保留原有方法签名）
        // ═══════════════════════════════════════════════════════════

        public async Task DeleteDeploymentAsync(string deploymentId, bool cascade)
        {
            if (string.IsNullOrWhiteSpace(deploymentId))
                throw new ArgumentException("deploymentId 不能为空");

            await _repositoryService.DeleteDeploymentAsync(deploymentId, cascade);
            _logger.LogInformation("部署已删除: {DeploymentId}", deploymentId);
        }

        // ═══════════════════════════════════════════════════════════
        // 私有：XML 解析
        // ═══════════════════════════════════════════════════════════

        private Dictionary<string, NodeSemanticInfo> ParseNodeSemantics(XDocument doc)
        {
            var result = new Dictionary<string, NodeSemanticInfo>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var task in doc.Descendants(BpmnNs + "userTask"))
            {
                var taskId = task.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(taskId)) continue;

                var fields = ParseFlowableFields(task);
                fields.TryGetValue("nodeSemantic", out var nodeSemantic);
                fields.TryGetValue("pageCode", out var pageCode);
                fields.TryGetValue("isConvergencePoint", out var convStr);
                bool.TryParse(convStr, out var isConvergencePoint);

                result[taskId] = new NodeSemanticInfo
                {
                    TaskDefinitionKey = taskId,
                    NodeSemantic = nodeSemantic,
                    PageCode = pageCode,
                    IsConvergencePoint = isConvergencePoint
                };
            }

            _logger.LogInformation(
                "BPMN 节点解析完成，userTask 数={Count}", result.Count);

            return result;
        }

        private Dictionary<string, string> ParseFlowableFields(XElement element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ext = element.Element(BpmnNs + "extensionElements");
            if (ext == null) return result;

            foreach (var field in ext.Elements(FlowableNs + "field"))
            {
                var name = field.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // 支持两种写法：stringValue 属性 或 <flowable:string> 子元素
                var value = field.Attribute("stringValue")?.Value
                            ?? field.Element(FlowableNs + "string")?.Value;
                if (value != null) result[name] = value;
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // 私有：slotConfig 解析、校验、合并
        // ═══════════════════════════════════════════════════════════

        private List<NodeSlotConfig> ParseSlotConfig(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<NodeSlotConfig>();
            try
            {
                return JsonSerializer.Deserialize<List<NodeSlotConfig>>(json, JsonOpts)
                       ?? new List<NodeSlotConfig>();
            }
            catch (Exception ex)
            {
                throw new BusinessException($"slotConfigJson 解析失败: {ex.Message}");
            }
        }

        private void ValidateSlotConfig(
            List<NodeSlotConfig> slotConfig,
            string processDefinitionKey)
        {
            if (!slotConfig.Any()) return;

            var errors = new List<string>();
            var allSlotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allRejectCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 预收集所有合法的 rejectCode（IsRejectTarget=true 的节点）
            var validRejectCodes = slotConfig
                .Where(n => n.IsRejectTarget && !string.IsNullOrWhiteSpace(n.RejectCode))
                .Select(n => n.RejectCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // isStarterNode 唯一性校验
            var starterCount = slotConfig.Count(n => n.IsStarterNode);
            if (starterCount > 1)
                errors.Add($"isStarterNode=true 的节点有 {starterCount} 个，只能有一个");

            foreach (var node in slotConfig)
            {
                if (string.IsNullOrWhiteSpace(node.TaskDefinitionKey))
                { errors.Add("存在 taskDefinitionKey 为空的配置项"); continue; }

                var key = node.TaskDefinitionKey;

                // Slot 校验：slotKey 全局唯一、variableName 必填、mode 合法
                foreach (var slot in node.Slots ?? new List<SlotDefinition>())
                {
                    if (string.IsNullOrWhiteSpace(slot.SlotKey))
                    { errors.Add($"节点 [{key}] 存在 slotKey 为空的 Slot"); continue; }
                    if (string.IsNullOrWhiteSpace(slot.VariableName))
                        errors.Add($"节点 [{key}] Slot [{slot.SlotKey}] variableName 不能为空");
                    if (slot.Mode != "single" && slot.Mode != "multiple")
                        errors.Add($"节点 [{key}] Slot [{slot.SlotKey}] mode 必须是 single 或 multiple");
                    if (!allSlotKeys.Add(slot.SlotKey))
                        errors.Add($"slotKey [{slot.SlotKey}] 重复，流程内全局唯一");
                }

                // 驳回目标校验：rejectCode 全局唯一
                if (node.IsRejectTarget)
                {
                    if (string.IsNullOrWhiteSpace(node.RejectCode))
                        errors.Add($"节点 [{key}] isRejectTarget=true 但 rejectCode 为空");
                    else if (!allRejectCodes.Add(node.RejectCode))
                        errors.Add($"rejectCode [{node.RejectCode}] 重复，流程内全局唯一");
                }

                // 驳回能力校验：rejectOptions 必填且引用合法
                if (node.CanReject)
                {
                    if (node.RejectOptions == null || !node.RejectOptions.Any())
                        errors.Add($"节点 [{key}] canReject=true 但 rejectOptions 为空");

                    foreach (var opt in node.RejectOptions ?? new List<RejectOptionConfig>())
                    {
                        if (string.IsNullOrWhiteSpace(opt.RejectCode))
                        { errors.Add($"节点 [{key}] rejectOption 的 rejectCode 不能为空"); continue; }
                        if (string.IsNullOrWhiteSpace(opt.Label))
                            errors.Add($"节点 [{key}] rejectOption [{opt.RejectCode}] label 不能为空");
                        if (!validRejectCodes.Contains(opt.RejectCode))
                            errors.Add(
                                $"节点 [{key}] rejectOption 引用了不存在的 rejectCode [{opt.RejectCode}]，" +
                                $"目标节点需配置 isRejectTarget=true 且 rejectCode 一致");
                    }
                }
            }

            if (errors.Any())
                throw new BusinessException(
                    $"slotConfig 校验失败: {string.Join("；", errors)}");
        }

        private void MergeSlotConfig(
            Dictionary<string, NodeSemanticInfo> nodeSemanticMap,
            List<NodeSlotConfig> slotConfig)
        {
            foreach (var node in slotConfig)
            {
                if (string.IsNullOrWhiteSpace(node.TaskDefinitionKey)) continue;

                if (!nodeSemanticMap.TryGetValue(node.TaskDefinitionKey, out var info))
                {
                    _logger.LogWarning(
                        "slotConfig [{Key}] 在 BPMN 中未找到对应 userTask，已跳过",
                        node.TaskDefinitionKey);
                    continue;
                }

                // slotConfig 中的语义字段优先级高于 XML extensionElements
                if (!string.IsNullOrWhiteSpace(node.NodeSemantic))
                    info.NodeSemantic = node.NodeSemantic;
                if (!string.IsNullOrWhiteSpace(node.PageCode))
                    info.PageCode = node.PageCode;

                info.IsStarterNode = node.IsStarterNode;
                info.IsConvergencePoint = node.IsConvergencePoint;

                info.CanReject = node.CanReject;
                info.RejectOptions = node.RejectOptions?
                    .Select(o => new RejectOption
                    {
                        RejectCode = o.RejectCode,
                        Label = o.Label,
                        Description = o.Description
                    }).ToList() ?? new List<RejectOption>();

                info.IsRejectTarget = node.IsRejectTarget;
                info.RejectCode = node.RejectCode;
                info.Slots = node.Slots ?? new List<SlotDefinition>();
            }
        }
    }
}