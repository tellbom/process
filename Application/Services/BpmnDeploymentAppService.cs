using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using process.Domain.DistributedLock;

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
        private readonly IWorkflowReliabilityStore _reliabilityStore;
        private readonly IDistributedLockService _distributedLockService;
        private readonly ICurrentUser _currentUser;
        private readonly Dm8Options _dm8Options;

        public BpmnDeploymentAppService(
            IFlowableRepositoryService repositoryService,
            IElasticSearchService esService,
            IWorkflowReliabilityStore reliabilityStore,
            IDistributedLockService distributedLockService,
            ICurrentUser currentUser,
            IOptions<Dm8Options> dm8Options,
            ILogger<BpmnDeploymentAppService> logger)
        {
            _repositoryService = repositoryService;
            _esService = esService;
            _reliabilityStore = reliabilityStore;
            _distributedLockService = distributedLockService;
            _currentUser = currentUser;
            _dm8Options = dm8Options.Value;
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
            string slotConfigJson,
            string deploymentRequestId = null,
            string businessVersion = null)
        {
            if (file == null || file.Length == 0)
                throw new BusinessException("BPMN 文件不能为空");
            if (!_dm8Options.Enabled)
                throw new BusinessException(
                    "生产化部署要求启用 DM8 幂等与状态门禁",
                    "DM8_RELIABILITY_REQUIRED");

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

            var configJson = JsonSerializer.Serialize(nodeSemanticMap, JsonOpts);
            var bpmnSha256 = ComputeSha256(CanonicalizeBpmn(doc));
            var configSha256 = ComputeSha256(configJson);
            var artifactSha256 = ComputeSha256(
                $"{bpmnSha256}:{configSha256}");
            businessVersion = NormalizeBusinessVersion(
                businessVersion,
                artifactSha256);
            deploymentRequestId = string.IsNullOrWhiteSpace(deploymentRequestId)
                ? $"{processDefinitionKey}:{businessVersion}"
                : deploymentRequestId.Trim();
            if (deploymentRequestId.Length > 480)
                throw new BusinessException(
                    "deploymentRequestId 长度不能超过 480");

            var deploymentIdempotencyKey =
                $"deployment:{processDefinitionKey}:{businessVersion}";
            var deploymentRequestIdempotencyKey =
                $"deployment-request:{deploymentRequestId}";
            var deploymentJournal = JsonSerializer.Serialize(
                new DeploymentJournalPayload
                {
                    DeploymentRequestId = deploymentRequestId,
                    ProcessDefinitionKey = processDefinitionKey,
                    BusinessVersion = businessVersion,
                    BpmnSha256 = bpmnSha256,
                    ConfigSha256 = configSha256,
                    ArtifactSha256 = artifactSha256,
                    FileName = fileName
                },
                JsonOpts);
            var lockKey = $"flow:deployment:{deploymentIdempotencyKey}";
            var lockValue = Guid.NewGuid().ToString("N");
            var lockAcquired = false;
            WorkflowTaskAction deploymentAction = null;
            WorkflowTaskAction deploymentRequestAction = null;
            var deploymentActionValidated = false;
            var deploymentRequestActionValidated = false;
            var flowableDeployed = false;
            var reusedFlowableDeployment = false;
            try
            {
                lockAcquired = await _distributedLockService.TryAcquireAsync(
                    lockKey,
                    lockValue,
                    TimeSpan.FromMinutes(5));
                if (!lockAcquired)
                {
                    throw new BusinessException(
                        $"流程 [{processDefinitionKey}] 业务版本 [{businessVersion}] 正在部署，请稍后使用相同请求重试",
                        "DEPLOYMENT_IN_PROGRESS");
                }

                deploymentRequestAction =
                    await _reliabilityStore.PrepareTaskActionAsync(
                        new PrepareTaskActionCommand
                        {
                            ActionId = Guid.NewGuid().ToString("N"),
                            IdempotencyKey =
                                deploymentRequestIdempotencyKey,
                            BusinessId =
                                $"deployment:{processDefinitionKey}",
                            ProcessInstanceId = businessVersion,
                            TaskDefinitionKey = processDefinitionKey,
                            ActionType = "deployment_request",
                            OperatorId =
                                _currentUser.UserId
                                ?? "authenticated-user",
                            RequestJson = deploymentJournal
                        });
                ValidateDeploymentRetry(
                    deploymentRequestAction,
                    deploymentJournal,
                    processDefinitionKey,
                    businessVersion);
                deploymentRequestActionValidated = true;

                deploymentAction = await _reliabilityStore.PrepareTaskActionAsync(
                    new PrepareTaskActionCommand
                    {
                        ActionId = Guid.NewGuid().ToString("N"),
                        IdempotencyKey = deploymentIdempotencyKey,
                        BusinessId = $"deployment:{processDefinitionKey}",
                        ProcessInstanceId = businessVersion,
                        TaskDefinitionKey = processDefinitionKey,
                        ActionType = "deployment",
                        OperatorId = _currentUser.UserId ?? "authenticated-user",
                        RequestJson = deploymentJournal
                    });

                ValidateDeploymentRetry(
                    deploymentAction,
                    deploymentJournal,
                    processDefinitionKey,
                    businessVersion);
                deploymentActionValidated = true;

                var latest = await _repositoryService
                    .GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);
                if (latest != null)
                {
                    var activeConfig = _dm8Options.Enabled
                        ? await _reliabilityStore.GetDefinitionConfigAsync(
                            processDefinitionKey,
                            latest.Version)
                        : null;
                    if (activeConfig != null
                        && string.Equals(
                            activeConfig.ProcessDefinitionId,
                            latest.Id,
                            StringComparison.Ordinal)
                        && string.Equals(
                            activeConfig.ContentHash,
                            artifactSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await _reliabilityStore.MarkTaskActionResultAsync(
                            deploymentAction.ActionId,
                            "applied",
                            "active",
                            null);
                        await _reliabilityStore.MarkTaskActionResultAsync(
                            deploymentRequestAction.ActionId,
                            "applied",
                            "active",
                            null);
                        return BuildDeploymentResponse(
                            deploymentRequestId,
                            businessVersion,
                            bpmnSha256,
                            reused: true,
                            latest.DeploymentId,
                            processDefinitionKey,
                            processDefinitionName,
                            latest.Version,
                            DateTime.UtcNow,
                            nodeSemanticMap);
                    }

                    if (deploymentAction.FlowableResult == "flowable_deployed"
                        || deploymentAction.ResultState
                            is "reconcile_required" or "failed")
                    {
                        var deployedXml = await _repositoryService
                            .GetBpmnXmlByDefinitionIdAsync(latest.Id);
                        if (!string.IsNullOrWhiteSpace(deployedXml)
                            && string.Equals(
                                ComputeSha256(CanonicalizeBpmn(
                                    XDocument.Parse(deployedXml))),
                                bpmnSha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            flowableDeployed = true;
                            reusedFlowableDeployment = true;
                        }
                    }
                }

                FlowableDeployment deployment;
                if (!flowableDeployed)
                {
                    deployment = await _repositoryService.DeployBpmnAsync(
                        fileName,
                        bpmnXml);
                    flowableDeployed = true;
                }
                else
                {
                    var reusedDefinition = await _repositoryService
                        .GetLatestProcessDefinitionByKeyAsync(
                            processDefinitionKey);
                    deployment = new FlowableDeployment
                    {
                        Id = reusedDefinition.DeploymentId,
                        Name = fileName,
                        DeploymentTime = DateTime.UtcNow
                    };
                }

                var processDefinition = await _repositoryService
                    .GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);

                if (processDefinition == null)
                    throw new BusinessException(
                        "Flowable 部署成功，但无法读取已部署的流程定义版本",
                        "FLOWABLE_DEFINITION_VERSION_MISSING");

                await _reliabilityStore.MarkTaskActionResultAsync(
                    deploymentAction.ActionId,
                    "reconcile_required",
                    "flowable_deployed",
                    null);
                await _reliabilityStore.MarkTaskActionResultAsync(
                    deploymentRequestAction.ActionId,
                    "reconcile_required",
                    "flowable_deployed",
                    null);

                // Existing WORKFLOW_DEFINITION_CONFIG rows are the ACTIVE
                // gate. This remains CRUD-only and requires no production DDL.
                if (_dm8Options.Enabled)
                {
                    await _reliabilityStore.SaveDefinitionConfigAsync(
                        new WorkflowDefinitionConfig
                        {
                            ProcessDefinitionKey = processDefinitionKey,
                            ProcessDefinitionVersion = processDefinition.Version,
                            ProcessDefinitionId = processDefinition.Id,
                            ContentHash = artifactSha256,
                            ConfigJson = configJson
                        });
                }

                try
                {
                    await _esService.SaveNodeSemanticMapAsync(
                        processDefinitionKey,
                        nodeSemanticMap);
                }
                catch (Exception projectionException)
                {
                    _logger.LogError(
                        projectionException,
                        "Deployment became ACTIVE but ES projection failed and must be rebuilt. DeploymentRequestId={DeploymentRequestId}, ProcessDefinitionKey={ProcessDefinitionKey}, BusinessVersion={BusinessVersion}, ProcessDefinitionId={ProcessDefinitionId}",
                        deploymentRequestId,
                        processDefinitionKey,
                        businessVersion,
                        processDefinition.Id);
                }

                await _reliabilityStore.MarkTaskActionResultAsync(
                    deploymentAction.ActionId,
                    "applied",
                    "active",
                    null);
                await _reliabilityStore.MarkTaskActionResultAsync(
                    deploymentRequestAction.ActionId,
                    "applied",
                    "active",
                    null);

                _logger.LogInformation(
                    "部署完成: DeploymentRequestId={DeploymentRequestId}, BusinessVersion={BusinessVersion}, BpmnSha256={BpmnSha256}, Key={Key}, Version={Version}, 节点数={Count}, DeploymentId={Id}",
                    deploymentRequestId,
                    businessVersion,
                    bpmnSha256,
                    processDefinitionKey,
                    processDefinition.Version,
                    nodeSemanticMap.Count,
                    deployment.Id);

                return BuildDeploymentResponse(
                    deploymentRequestId,
                    businessVersion,
                    bpmnSha256,
                    reused: reusedFlowableDeployment,
                    deployment.Id,
                    processDefinitionKey,
                    processDefinitionName,
                    processDefinition.Version,
                    deployment.DeploymentTime,
                    nodeSemanticMap);
            }
            catch (Exception ex)
            {
                if (deploymentAction != null && deploymentActionValidated)
                {
                    try
                    {
                        await _reliabilityStore.MarkTaskActionResultAsync(
                            deploymentAction.ActionId,
                            flowableDeployed ? "reconcile_required" : "failed",
                            flowableDeployed
                                ? "flowable_deployed"
                                : "flowable_not_deployed",
                            ex.Message);
                    }
                    catch (Exception journalException)
                    {
                        _logger.LogError(
                            journalException,
                            "Failed to persist deployment failure state. DeploymentRequestId={DeploymentRequestId}",
                            deploymentRequestId);
                    }
                }
                if (deploymentRequestAction != null
                    && deploymentRequestActionValidated)
                {
                    try
                    {
                        await _reliabilityStore.MarkTaskActionResultAsync(
                            deploymentRequestAction.ActionId,
                            flowableDeployed
                                ? "reconcile_required"
                                : "failed",
                            flowableDeployed
                                ? "flowable_deployed"
                                : "flowable_not_deployed",
                            ex.Message);
                    }
                    catch (Exception journalException)
                    {
                        _logger.LogError(
                            journalException,
                            "Failed to persist deployment request failure state. DeploymentRequestId={DeploymentRequestId}",
                            deploymentRequestId);
                    }
                }

                if (ex is BusinessException)
                    throw;
                throw new BusinessException(
                    $"BPMN 部署失败: {ex.Message}",
                    flowableDeployed
                        ? "DEPLOYMENT_SYNC_INCOMPLETE"
                        : "FLOWABLE_DEPLOYMENT_FAILED");
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        await _distributedLockService.ReleaseAsync(
                            lockKey,
                            lockValue);
                    }
                    catch (Exception releaseException)
                    {
                        _logger.LogWarning(
                            releaseException,
                            "Failed to release deployment coordination lock. LockKey={LockKey}",
                            lockKey);
                    }
                }
            }
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
                CanReassign = n.CanReassign,
                RejectOptions = n.RejectOptions ?? new List<RejectOption>(),
                IsRejectTarget = n.IsRejectTarget,
                RejectCode = n.RejectCode,
                Slots = n.Slots ?? new List<SlotDefinition>(),
                RoleKey = n.RoleKey,
                AssigneeMode = n.AssigneeMode,
                CallbackUrl = n.CallbackUrl
            }).ToList();
        }

        public async Task<BpmnDeploymentStatusResponse> GetDeploymentStatusAsync(
            string processDefinitionKey,
            string businessVersion)
        {
            if (string.IsNullOrWhiteSpace(processDefinitionKey)
                || string.IsNullOrWhiteSpace(businessVersion))
                throw new BusinessException(
                    "processDefinitionKey 和 businessVersion 不能为空");

            var action = await _reliabilityStore
                .GetTaskActionByIdempotencyKeyAsync(
                    $"deployment:{processDefinitionKey}:{businessVersion}");
            if (action == null)
                throw new BusinessException(
                    $"未找到流程 [{processDefinitionKey}] 业务版本 [{businessVersion}] 的部署记录",
                    "DEPLOYMENT_NOT_FOUND");

            DeploymentJournalPayload journal = null;
            try
            {
                journal = JsonSerializer.Deserialize<DeploymentJournalPayload>(
                    action.RequestJson ?? string.Empty,
                    JsonOpts);
            }
            catch
            {
                // The journal state below remains visible even if an old row
                // contains malformed request JSON.
            }

            var latest = await _repositoryService
                .GetLatestProcessDefinitionByKeyAsync(processDefinitionKey);
            WorkflowDefinitionConfig active = null;
            if (latest != null)
            {
                active = await _reliabilityStore.GetDefinitionConfigAsync(
                    processDefinitionKey,
                    latest.Version);
            }
            var canStart = action.ResultState == "applied"
                           && action.FlowableResult == "active"
                           && latest != null
                           && active != null
                           && string.Equals(
                               active.ProcessDefinitionId,
                               latest.Id,
                               StringComparison.Ordinal);

            return new BpmnDeploymentStatusResponse
            {
                ProcessDefinitionKey = processDefinitionKey,
                BusinessVersion = businessVersion,
                DeploymentRequestId = journal?.DeploymentRequestId,
                BpmnSha256 = journal?.BpmnSha256,
                Status = canStart
                    ? "ACTIVE"
                    : action.ResultState.ToUpperInvariant(),
                CompletedStep = action.FlowableResult,
                LastError = action.LastError,
                ProcessDefinitionId = latest?.Id,
                FlowableVersion = latest?.Version,
                CanStart = canStart
            };
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
                fields.TryGetValue("roleKey", out var roleKey);
                fields.TryGetValue("assigneeMode", out var assigneeMode);
                fields.TryGetValue("canReassign", out var canReassignStr);
                bool.TryParse(canReassignStr, out var canReassign);
                fields.TryGetValue("callbackUrl", out var callbackUrl);
                var callbackUrlSpecified = fields.ContainsKey("callbackUrl");

                result[taskId] = new NodeSemanticInfo
                {
                    TaskDefinitionKey = taskId,
                    NodeSemantic = nodeSemantic,
                    PageCode = pageCode,
                    IsConvergencePoint = isConvergencePoint,
                    RoleKey = roleKey,
                    AssigneeMode = assigneeMode,
                    CanReassign = canReassign,
                    CallbackUrl = callbackUrl,
                    CallbackUrlSpecified = callbackUrlSpecified
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
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new BusinessException("slotConfigJson 必须是数组");

                var result = new List<NodeSlotConfig>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var node = element.Deserialize<NodeSlotConfig>(JsonOpts);
                    if (node == null) continue;
                    node.CallbackUrlSpecified = HasJsonProperty(element, "callbackUrl");
                    result.Add(node);
                }

                return result;
            }
            catch (BusinessException) { throw; }
            catch (Exception ex)
            {
                throw new BusinessException($"slotConfigJson 解析失败: {ex.Message}");
            }
        }

        private static bool HasJsonProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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

                // Slot 校验：slotKey 全局唯一、roleKey/variableName 必填、mode 合法
                foreach (var slot in node.Slots ?? new List<SlotDefinition>())
                {
                    if (string.IsNullOrWhiteSpace(slot.SlotKey))
                    { errors.Add($"节点 [{key}] 存在 slotKey 为空的 Slot"); continue; }
                    if (string.IsNullOrWhiteSpace(slot.RoleKey))
                        errors.Add($"节点 [{key}] Slot [{slot.SlotKey}] roleKey 不能为空");
                    if (string.IsNullOrWhiteSpace(slot.VariableName))
                        errors.Add($"节点 [{key}] Slot [{slot.SlotKey}] variableName 不能为空");
                    if (slot.Mode != "single" && slot.Mode != "multiple")
                        errors.Add($"节点 [{key}] Slot [{slot.SlotKey}] mode 必须是 single 或 multiple");
                    if (!allSlotKeys.Add(slot.SlotKey))
                        errors.Add($"slotKey [{slot.SlotKey}] 重复，流程内全局唯一");
                }

                // 驳回目标校验：rejectCode 全局唯一
                if (!string.IsNullOrWhiteSpace(node.AssigneeMode)
                    && node.AssigneeMode != "single"
                    && node.AssigneeMode != "multiple")
                {
                    errors.Add($"节点 [{key}] assigneeMode 必须是 single 或 multiple，当前值：{node.AssigneeMode}");
                }

                if (!string.IsNullOrWhiteSpace(node.CallbackUrl)
                    && !Uri.TryCreate(node.CallbackUrl, UriKind.Absolute, out _))
                {
                    _logger.LogWarning(
                        "节点 [{Key}] callbackUrl 不是绝对 URL，请确认配置正确: {Url}",
                        key,
                        node.CallbackUrl);
                }

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
                info.CanReassign = node.CanReassign;
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

                if (!string.IsNullOrWhiteSpace(node.RoleKey))
                    info.RoleKey = node.RoleKey;
                if (!string.IsNullOrWhiteSpace(node.AssigneeMode))
                    info.AssigneeMode = node.AssigneeMode;

                if (node.CallbackUrlSpecified)
                {
                    info.CallbackUrl = node.CallbackUrl;
                    info.CallbackUrlSpecified = true;
                }
            }
        }

        private static string CanonicalizeBpmn(XDocument document)
            => document.ToString(SaveOptions.DisableFormatting);

        private static string ComputeSha256(string value)
            => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private static string NormalizeBusinessVersion(
            string businessVersion,
            string artifactSha256)
        {
            var resolved = string.IsNullOrWhiteSpace(businessVersion)
                ? $"auto-{artifactSha256[..16].ToLowerInvariant()}"
                : businessVersion.Trim();
            if (resolved.Length > 100)
                throw new BusinessException(
                    "businessVersion 长度不能超过 100");
            return resolved;
        }

        private static void ValidateDeploymentRetry(
            WorkflowTaskAction action,
            string expectedRequestJson,
            string processDefinitionKey,
            string businessVersion)
        {
            DeploymentJournalPayload existing;
            DeploymentJournalPayload expected;
            try
            {
                existing = JsonSerializer.Deserialize<DeploymentJournalPayload>(
                    action.RequestJson ?? string.Empty,
                    JsonOpts);
                expected = JsonSerializer.Deserialize<DeploymentJournalPayload>(
                    expectedRequestJson,
                    JsonOpts);
            }
            catch (Exception exception)
            {
                throw new BusinessException(
                    $"部署幂等记录无法解析: {exception.Message}",
                    "DEPLOYMENT_JOURNAL_INVALID");
            }

            if (existing == null
                || expected == null
                || !string.Equals(
                    existing.ProcessDefinitionKey,
                    expected.ProcessDefinitionKey,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    existing.BusinessVersion,
                    expected.BusinessVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.ArtifactSha256,
                    expected.ArtifactSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"流程 [{processDefinitionKey}] 的业务版本 [{businessVersion}] 已绑定其他 BPMN 或节点配置，禁止覆盖",
                    "DEPLOYMENT_VERSION_CONTENT_CONFLICT");
            }
        }

        private static BpmnDeploymentResponse BuildDeploymentResponse(
            string deploymentRequestId,
            string businessVersion,
            string bpmnSha256,
            bool reused,
            string deploymentId,
            string processDefinitionKey,
            string processDefinitionName,
            int version,
            DateTime deploymentTime,
            Dictionary<string, NodeSemanticInfo> nodeSemanticMap)
            => new()
            {
                DeploymentRequestId = deploymentRequestId,
                BusinessVersion = businessVersion,
                BpmnSha256 = bpmnSha256,
                DeploymentStatus = "ACTIVE",
                Reused = reused,
                DeploymentId = deploymentId,
                ProcessDefinitionKey = processDefinitionKey,
                ProcessDefinitionName = processDefinitionName,
                Version = version,
                DeploymentTime = deploymentTime,
                NodeSemanticCount = nodeSemanticMap.Count,
                Nodes = nodeSemanticMap.Values.Select(n => new NodeSemanticSummary
                {
                    TaskDefinitionKey = n.TaskDefinitionKey,
                    NodeSemantic = n.NodeSemantic,
                    PageCode = n.PageCode,
                    IsStarterNode = n.IsStarterNode,
                    IsConvergencePoint = n.IsConvergencePoint,
                    CanReject = n.CanReject,
                    CanReassign = n.CanReassign,
                    IsRejectTarget = n.IsRejectTarget,
                    RejectCode = n.RejectCode,
                    SlotCount = n.Slots?.Count ?? 0,
                    RejectOptionCount = n.RejectOptions?.Count ?? 0,
                    RoleKey = n.RoleKey,
                    AssigneeMode = n.AssigneeMode,
                    CallbackUrl = n.CallbackUrl
                }).ToList()
            };

        private sealed class DeploymentJournalPayload
        {
            public string DeploymentRequestId { get; init; }
            public string ProcessDefinitionKey { get; init; }
            public string BusinessVersion { get; init; }
            public string BpmnSha256 { get; init; }
            public string ConfigSha256 { get; init; }
            public string ArtifactSha256 { get; init; }
            public string FileName { get; init; }
        }
    }
}
