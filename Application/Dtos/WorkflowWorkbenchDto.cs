using FlowableWrapper.Domain.Flowable;

namespace FlowableWrapper.Application.Dtos;

public sealed class WorkflowArtifactRequest
{
    public string BpmnXml { get; set; } = "";
    public string? SlotConfigJson { get; set; }
}

public sealed class WorkflowArtifactIssue
{
    public string Severity { get; set; } = "error";
    public string Code { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Field { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class WorkflowArtifactResult
{
    public bool Valid => Issues.All(x => x.Severity != "error");
    public string ProcessDefinitionKey { get; set; } = "";
    public string ProcessDefinitionName { get; set; } = "";
    public string BpmnXml { get; set; } = "";
    public string SlotConfigJson { get; set; } = "[]";
    public List<WorkflowArtifactIssue> Issues { get; set; } = new();
}

public sealed class WorkflowDefinitionPage
{
    public List<FlowableProcessDefinition> Items { get; set; } = new();
    public int Total { get; set; }
}

public sealed class WorkflowDefinitionArtifact
{
    public FlowableProcessDefinition Definition { get; set; } = new();
    public string BpmnXml { get; set; } = "";
    public string SlotConfigJson { get; set; } = "[]";
    public string ConfigurationSource { get; set; } = "flowable";
}

public sealed class WorkflowDefinitionReadiness
{
    public FlowableProcessDefinition? Definition { get; set; }
    public bool CanStart => Checks.Count > 0 && Checks.All(x => x.Passed);
    public List<WorkflowDefinitionGate> Checks { get; set; } = new();
}

public sealed class WorkflowDefinitionGate
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
    public string Action { get; set; } = "";
}
