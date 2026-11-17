using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;

namespace FlowableWrapper.Application.Services;

/// <summary>只读推演普通变量条件；不执行 JUEL、服务任务，也不推进真实实例。</summary>
internal static class WorkflowNextStepResolver
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Flowable = "http://flowable.org/bpmn";
    private static readonly Regex Condition = new(@"^\$\{\s*(?<neg>!)?\s*(?<name>[A-Za-z_]\w*)\s*(?:(?<op>==|!=|>=|<=|>|<)\s*(?<value>true|false|null|-?\d+(?:\.\d+)?|'[^']*'|""[^""]*""))?\s*\}$", RegexOptions.CultureInvariant);

    internal static WorkflowNextStepDto Resolve(string xml, string currentNodeId, Dictionary<string, object> variables, List<SlotDefinition> slots)
    {
        var result = new WorkflowNextStepDto();
        var doc = WorkflowArtifactCompiler.Parse(xml);
        var nodes = doc.Descendants().Where(x => x.Name.Namespace == Bpmn && x.Attribute("id") != null)
            .ToDictionary(x => (string)x.Attribute("id")!, StringComparer.Ordinal);
        if (!nodes.TryGetValue(currentNodeId, out var current))
        {
            result.Message = "实例绑定的流程中找不到当前节点，请刷新执行状态。";
            return result;
        }
        var flows = doc.Descendants(Bpmn + "sequenceFlow").ToList();
        var allTargets = new Dictionary<string, WorkflowNextNode>();
        var seenAll = new HashSet<string>();
        void Collect(XElement node)
        {
            var id = (string)node.Attribute("id")!;
            if (!seenAll.Add(id)) return;
            if (node.Name == Bpmn + "userTask") { allTargets[id] = Target(node); return; }
            foreach (var edge in flows.Where(x => (string?)x.Attribute("sourceRef") == id))
                if (nodes.TryGetValue((string)edge.Attribute("targetRef")!, out var next)) Collect(next);
        }
        foreach (var edge in flows.Where(x => (string?)x.Attribute("sourceRef") == currentNodeId))
            if (nodes.TryGetValue((string)edge.Attribute("targetRef")!, out var next)) Collect(next);

        var unresolved = false;
        var visited = new HashSet<string>();
        void Fail(string message, bool manual = false) { unresolved = true; result.ManualConfigurationRequired |= manual; if (string.IsNullOrEmpty(result.Message)) result.Message = message; }
        bool? Evaluate(string expression)
        {
            var match = Condition.Match(expression.Trim());
            if (!match.Success) { Fail("此分支包含复杂表达式，暂不能自动确定选人范围，可在高级配置中编辑完整请求：" + expression, true); return null; }
            var name = match.Groups["name"].Value;
            var literal = match.Groups["value"].Value;
            var type = literal is "true" or "false" || !match.Groups["op"].Success ? "boolean" : literal.StartsWith("'") || literal.StartsWith('"') ? "string" : literal == "null" ? "json" : "number";
            var exists = variables.TryGetValue(name, out var value);
            if (!result.Variables.Any(x => x.Name == name)) result.Variables.Add(new() { Name = name, Type = type, Missing = !exists });
            if (!exists) { Fail("请先填写分支变量，再确定下一步处理人。"); return null; }
            value = Unwrap(value);
            bool matched;
            if (!match.Groups["op"].Success)
            {
                if (!bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out matched))
                { Fail($"变量 {name} 应填写 true 或 false。"); return null; }
                return match.Groups["neg"].Success ? !matched : matched;
            }
            if (match.Groups["neg"].Success) { Fail("此比较条件需要在高级配置中手动办理，或改用明确的 ${flag != true}。", true); return null; }
            var op = match.Groups["op"].Value;
            int comparison;
            if (type == "boolean")
            {
                if (!bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var actual))
                { Fail($"变量 {name} 应填写 true 或 false。"); return null; }
                comparison = actual.CompareTo(bool.Parse(literal));
            }
            else if (type == "number")
            {
                if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                { Fail($"变量 {name} 应填写数字。"); return null; }
                comparison = number.CompareTo(decimal.Parse(literal, CultureInfo.InvariantCulture));
            }
            else if (literal == "null") comparison = value == null ? 0 : 1;
            else
            {
                if (value is not string && value != null) { Fail($"变量 {name} 应填写文本。"); return null; }
                comparison = string.CompareOrdinal(Convert.ToString(value, CultureInfo.InvariantCulture), literal[1..^1]);
            }
            return op switch { "==" => comparison == 0, "!=" => comparison != 0, ">" => comparison > 0, "<" => comparison < 0, ">=" => comparison >= 0, "<=" => comparison <= 0, _ => false };
        }
        void Visit(XElement node, bool afterService, bool origin = false)
        {
            var id = (string)node.Attribute("id")!;
            if (!origin && node.Name == Bpmn + "userTask")
            { if (!result.NextNodes.Any(x => x.Id == id)) result.NextNodes.Add(Target(node)); return; }
            if (!visited.Add(id + ":" + afterService)) return;
            if (node.Name.LocalName is "subProcess" or "callActivity" or "eventBasedGateway")
            { Fail("后续包含子流程或事件网关，请在高级配置中按原契约手动办理。", true); return; }
            afterService |= node.Name == Bpmn + "serviceTask" || node.Name == Bpmn + "scriptTask";
            var outgoing = flows.Where(x => (string?)x.Attribute("sourceRef") == id).ToList();
            var selected = new List<XElement>();
            var defaultId = (string?)node.Attribute("default");
            var exclusive = node.Name == Bpmn + "exclusiveGateway";
            if (outgoing.Any(x => x.Element(Bpmn + "conditionExpression") != null) && afterService)
            { Fail("分支前有服务或脚本节点，变量可能发生变化，请在高级配置中按原契约手动办理。", true); return; }
            foreach (var edge in outgoing.Where(x => (string?)x.Attribute("id") != defaultId))
            {
                var condition = edge.Element(Bpmn + "conditionExpression")?.Value;
                var matches = string.IsNullOrWhiteSpace(condition) || node.Name == Bpmn + "parallelGateway" ? true : Evaluate(condition);
                // 排他网关按 BPMN 声明顺序选第一个命中的出口；未知条件不能跳过或回退默认分支。
                if (matches == null) { if (exclusive) break; continue; }
                if (matches == true) { selected.Add(edge); if (exclusive) break; }
            }
            if (!selected.Any() && !unresolved && defaultId != null) selected.AddRange(outgoing.Where(x => (string?)x.Attribute("id") == defaultId));
            if (!selected.Any() && outgoing.Any() && !unresolved) Fail("当前变量没有命中任何出口，请核对分支条件或设置默认分支。");
            foreach (var edge in selected)
            {
                result.FlowIds.Add((string)edge.Attribute("id")!);
                if (nodes.TryGetValue((string)edge.Attribute("targetRef")!, out var next)) Visit(next, afterService);
            }
        }
        Visit(current, false, true);
        result.Resolved = !unresolved;
        if (result.Resolved)
        {
            result.Slots = slots.Where(slot =>
            {
                var targets = allTargets.Values.Where(x => x.AssigneeVariables.Contains(slot.VariableName)).ToList();
                return targets.Count == 0 || targets.Any(target => result.NextNodes.Any(x => x.Id == target.Id));
            }).ToList();
            result.Message = result.NextNodes.Any() ? "下一步：" + string.Join("、", result.NextNodes.Select(x => x.Name)) : "当前节点之后没有需要办理的人工节点。";
        }
        else { result.NextNodes.Clear(); result.FlowIds.Clear(); }
        return result;
    }

    private static WorkflowNextNode Target(XElement node)
    {
        var result = new WorkflowNextNode { Id = (string)node.Attribute("id")!, Name = (string?)node.Attribute("name") ?? (string)node.Attribute("id")! };
        var expressions = new[] { (string?)node.Attribute(Flowable + "assignee"), (string?)node.Element(Bpmn + "multiInstanceLoopCharacteristics")?.Attribute(Flowable + "collection") };
        foreach (var expression in expressions.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var match = Regex.Match(expression!, @"^(?:\$\{\s*)?([A-Za-z_]\w*)(?:\s*\})?$");
            if (match.Success) result.AssigneeVariables.Add(match.Groups[1].Value);
        }
        return result;
    }

    private static object? Unwrap(object? value) => value is JsonElement json ? json.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => json.GetDecimal(), JsonValueKind.String => json.GetString(), JsonValueKind.Null => null, _ => json.GetRawText() } : value;
}
