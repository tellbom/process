using FlowableWrapper.Domain.ElasticSearch;

namespace FlowableWrapper.Application.Dtos;

public sealed class WorkflowNextStepRequest
{
    public string BusinessId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public Dictionary<string, object> BusinessVariables { get; set; } = new();
}

public sealed class WorkflowNextStepDto
{
    public bool Resolved { get; set; }
    public bool ManualConfigurationRequired { get; set; }
    public string Message { get; set; } = "";
    public List<WorkflowBranchVariable> Variables { get; set; } = new();
    public List<WorkflowNextNode> NextNodes { get; set; } = new();
    public List<SlotDefinition> Slots { get; set; } = new();
    public List<string> FlowIds { get; set; } = new();
}

public sealed class WorkflowBranchVariable
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "json";
    public bool Missing { get; set; }
}

public sealed class WorkflowNextNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> AssigneeVariables { get; set; } = new();
}
