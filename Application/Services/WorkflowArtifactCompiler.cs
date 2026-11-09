using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using FlowableWrapper.Application.Dtos;

namespace FlowableWrapper.Application.Services;

/// <summary>无副作用的发布预检。节点配置随 BPMN 资源进入 Flowable，数据库只保留投射。</summary>
public static class WorkflowArtifactCompiler
{
    public static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    public static readonly XNamespace Configuration = "urn:process-center:configuration:1";
    private static readonly XNamespace Flowable = "http://flowable.org/bpmn";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static XDocument Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }

    public static string? ReadEmbeddedConfiguration(XDocument document)
        => document.Root?.Element(Bpmn + "process")?.Element(Bpmn + "extensionElements")
            ?.Element(Configuration + "nodeConfigurations")?.Value;

    public static WorkflowArtifactResult Compile(WorkflowArtifactRequest request)
    {
        var result = new WorkflowArtifactResult();
        void Issue(string code, string id, string field, string message, string severity = "error")
            => result.Issues.Add(new() { Code = code, ElementId = id, Field = field, Message = message, Severity = severity });
        XDocument doc;
        try { doc = Parse(request.BpmnXml); }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        {
            Issue("INVALID_XML", "", "bpmnXml", "BPMN XML 无法解析：" + ex.Message);
            return result;
        }
        var processes = doc.Root?.Elements(Bpmn + "process").ToList() ?? new();
        if (processes.Count != 1)
        {
            Issue("PROCESS_REQUIRED", "", "process", "每次发布必须包含且仅包含一个流程定义。");
            return result;
        }
        var process = processes[0];
        result.ProcessDefinitionKey = (string?)process.Attribute("id") ?? "";
        result.ProcessDefinitionName = (string?)process.Attribute("name") ?? "";
        if (string.IsNullOrWhiteSpace(result.ProcessDefinitionKey)) Issue("PROCESS_KEY_REQUIRED", "", "id", "流程标识不能为空。");
        if ((string?)process.Attribute("isExecutable") != "true") Issue("NOT_EXECUTABLE", result.ProcessDefinitionKey, "isExecutable", "流程必须设置 isExecutable=true。");

        var elements = doc.Descendants().Where(x => x.Attribute("id") != null).ToList();
        foreach (var group in elements.GroupBy(x => (string)x.Attribute("id")!, StringComparer.Ordinal))
            if (group.Count() > 1) Issue("DUPLICATE_ID", group.Key, "id", "BPMN 元素标识重复。");
        var nodes = process.Descendants().Where(x => x.Name.Namespace == Bpmn && x.Attribute("id") != null)
            .GroupBy(x => (string)x.Attribute("id")!).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var scope in process.DescendantsAndSelf().Where(x => x == process || x.Name == Bpmn + "subProcess"))
        {
            var id = (string?)scope.Attribute("id") ?? "";
            if ((string?)scope.Attribute("triggeredByEvent") == "true") continue;
            if (!scope.Elements(Bpmn + "startEvent").Any()) Issue("START_REQUIRED", id, "startEvent", "流程或子流程缺少开始事件。");
            if (!scope.Elements(Bpmn + "endEvent").Any()) Issue("END_REQUIRED", id, "endEvent", "流程或子流程缺少结束事件。");
        }
        foreach (var flow in process.Descendants(Bpmn + "sequenceFlow"))
        {
            var id = (string?)flow.Attribute("id") ?? "";
            foreach (var reference in new[] { "sourceRef", "targetRef" })
                if (!nodes.TryGetValue((string?)flow.Attribute(reference) ?? "", out var target) || target.Parent != flow.Parent)
                    Issue("INVALID_FLOW_REFERENCE", id, reference, "连线引用的节点不存在或不在同一个子流程内。");
        }
        foreach (var gateway in process.Descendants(Bpmn + "exclusiveGateway"))
        {
            var id = (string?)gateway.Attribute("id") ?? "";
            var outgoing = gateway.Parent!.Elements(Bpmn + "sequenceFlow").Where(x => (string?)x.Attribute("sourceRef") == id).ToList();
            if (outgoing.Count > 1)
                foreach (var flow in outgoing)
                    if ((string?)gateway.Attribute("default") != (string?)flow.Attribute("id") && string.IsNullOrWhiteSpace(flow.Element(Bpmn + "conditionExpression")?.Value))
                        Issue("BRANCH_CONDITION_REQUIRED", (string?)flow.Attribute("id") ?? "", "conditionExpression", "排他分支必须有条件，或被设为默认分支。");
        }

        var configJson = string.IsNullOrWhiteSpace(request.SlotConfigJson)
            ? ReadEmbeddedConfiguration(doc) ?? "[]" : request.SlotConfigJson;
        List<NodeSlotConfig> config;
        try
        {
            using var parsed = JsonDocument.Parse(configJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("根元素必须是数组");
            config = JsonSerializer.Deserialize<List<NodeSlotConfig>>(configJson, Json) ?? new();
            if (config.Any(x => x == null)) throw new JsonException("节点配置不能为 null");
            foreach (var entry in parsed.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("inputHints", out var hints)) continue;
                var id = entry.TryGetProperty("taskDefinitionKey", out var taskKey) ? taskKey.GetString() ?? "" : "";
                if (hints.ValueKind != JsonValueKind.Array)
                {
                    Issue("INVALID_INPUT_HINT", id, "inputHints", "联调输入提示必须是数组。");
                    continue;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hint in hints.EnumerateArray())
                {
                    if (hint.ValueKind != JsonValueKind.Object
                        || !hint.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
                        || !hint.TryGetProperty("stage", out var stage) || stage.ValueKind != JsonValueKind.String || stage.GetString() is not ("start" or "complete")
                        || (hint.TryGetProperty("description", out var description) && description.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        Issue("INVALID_INPUT_HINT", id, "inputHints", "联调输入提示需要变量名称、start / complete 阶段和文本说明。");
                    else if (!seen.Add(stage.GetString() + ":" + name.GetString()))
                        Issue("DUPLICATE_INPUT_HINT", id, "inputHints", "同一节点同一填写阶段的变量提示重复。");
                }
            }
        }
        catch (JsonException ex)
        {
            Issue("INVALID_CONFIGURATION", "", "slotConfigJson", "节点 JSON 无法解析：" + ex.Message);
            return result;
        }
        var tasks = process.Descendants(Bpmn + "userTask").ToList();
        var taskIds = tasks.Select(x => (string?)x.Attribute("id") ?? "").ToHashSet(StringComparer.Ordinal);
        var configuredIds = new HashSet<string>(StringComparer.Ordinal);
        var slotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectCodes = config.Where(x => x.IsRejectTarget && !string.IsNullOrWhiteSpace(x.RejectCode))
            .GroupBy(x => x.RejectCode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        if (config.Count(x => x.IsStarterNode) > 1) Issue("MULTIPLE_STARTERS", "", "isStarterNode", "只能配置一个发起人节点。");
        foreach (var node in config)
        {
            var id = node.TaskDefinitionKey ?? "";
            if (!taskIds.Contains(id)) Issue("UNKNOWN_TASK", id, "taskDefinitionKey", "JSON 配置没有对应的 BPMN 人工节点。");
            if (!configuredIds.Add(id)) Issue("DUPLICATE_CONFIGURATION", id, "taskDefinitionKey", "同一个节点存在重复 JSON 配置。");
            if (node.AssigneeMode is not (null or "" or "single" or "multiple")) Issue("INVALID_ASSIGNEE_MODE", id, "assigneeMode", "办理模式必须为 single 或 multiple。");
            if (node.IsRejectTarget && (string.IsNullOrWhiteSpace(node.RejectCode) || rejectCodes[node.RejectCode] != 1))
                Issue("INVALID_REJECT_TARGET", id, "rejectCode", "驳回目标编码必须填写且唯一。");
            if (node.CanReject && !(node.RejectOptions?.Count > 0)) Issue("REJECT_OPTIONS_REQUIRED", id, "rejectOptions", "允许驳回时必须选择驳回目标。");
            foreach (var option in node.RejectOptions ?? new())
                if (option == null || string.IsNullOrWhiteSpace(option.RejectCode) || !rejectCodes.ContainsKey(option.RejectCode) || string.IsNullOrWhiteSpace(option.Label))
                    Issue("INVALID_REJECT_OPTION", id, "rejectOptions", "驳回选项必须引用有效目标且填写名称。");
            foreach (var slot in node.Slots ?? new())
            {
                if (slot == null) { Issue("INVALID_SLOT", id, "slots", "选人槽位不能为 null。"); continue; }
                if (string.IsNullOrWhiteSpace(slot.SlotKey) || !slotIds.Add(slot.SlotKey)) Issue("INVALID_SLOT_KEY", id, "slots.slotKey", "选人槽位编码必须填写且全流程唯一。");
                if (string.IsNullOrWhiteSpace(slot.RoleKey) || string.IsNullOrWhiteSpace(slot.VariableName)) Issue("INVALID_SLOT_BINDING", id, "slots", "选人槽位必须绑定角色和流程变量。");
                if (slot.Mode is not ("single" or "multiple")) Issue("INVALID_SLOT_MODE", id, "slots.mode", "选人槽位模式必须为 single 或 multiple。");
            }
        }
        foreach (var task in tasks)
        {
            var id = (string?)task.Attribute("id") ?? "";
            if (!configuredIds.Contains(id)) Issue("MISSING_TASK_CONFIGURATION", id, "taskDefinitionKey", "此人工节点尚未配置业务属性。", "warning");
            var loop = task.Element(Bpmn + "multiInstanceLoopCharacteristics");
            var collection = (string?)loop?.Attribute(Flowable + "collection") ?? "";
            var collectionVariable = Regex.Match(collection, @"^\$\{\s*([A-Za-z_]\w*)\s*\}$");
            var variable = collectionVariable.Success ? collectionVariable.Groups[1].Value : collection;
            foreach (var owner in config)
                foreach (var slot in owner.Slots ?? new())
                    if (slot != null && loop != null && slot.VariableName == variable && slot.Mode == "single")
                        Issue("SLOT_COLLECTION_MODE_MISMATCH", owner.TaskDefinitionKey, "slots.mode", $"选人变量 [{variable}] 被多人节点 [{id}] 作为集合使用，槽位应使用 multiple。");
            if (loop != null && string.IsNullOrWhiteSpace((string?)loop.Attribute(Flowable + "collection")))
                Issue("COLLECTION_REQUIRED", id, "collection", "多人节点缺少 Flowable 集合变量。");
            if (loop != null && string.IsNullOrWhiteSpace((string?)loop.Attribute(Flowable + "elementVariable")))
                Issue("ELEMENT_VARIABLE_REQUIRED", id, "elementVariable", "多人节点缺少单人变量。");
            if (string.IsNullOrWhiteSpace((string?)task.Attribute(Flowable + "assignee")) && string.IsNullOrWhiteSpace((string?)task.Attribute(Flowable + "candidateUsers")) && string.IsNullOrWhiteSpace((string?)task.Attribute(Flowable + "candidateGroups")))
                Issue("ASSIGNEE_REQUIRED", id, "assignee", "人工节点缺少处理人或候选人配置。");
        }
        if (!doc.Descendants().Any(x => x.Name.LocalName == "BPMNDiagram")) Issue("LAYOUT_MISSING", "", "diagram", "文件没有图形坐标，导入画布时将尝试自动布局。", "warning");
        // 保留 JSON 中未被当前版本识别的扩展字段及 callbackUrl 的缺省/显式空值语义。
        using (var jsonDoc = JsonDocument.Parse(configJson)) result.SlotConfigJson = JsonSerializer.Serialize(jsonDoc.RootElement, Json).Replace("\r\n", "\n");
        var ext = process.Element(Bpmn + "extensionElements");
        if (ext == null) { ext = new XElement(Bpmn + "extensionElements"); process.AddFirst(ext); }
        ext.Elements(Configuration + "nodeConfigurations").Remove();
        ext.Add(new XElement(Configuration + "nodeConfigurations", result.SlotConfigJson));
        doc.Root!.SetAttributeValue(XNamespace.Xmlns + "pc", Configuration.NamespaceName);
        result.BpmnXml = doc.ToString();
        return result;
    }
}
