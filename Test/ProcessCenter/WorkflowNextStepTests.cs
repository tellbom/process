using System.Reflection;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using WorkflowSlot = FlowableWrapper.Domain.ElasticSearch.SlotDefinition;

namespace FlowableWrapper.Test.ProcessCenter;

public class WorkflowNextStepTests
{
    private const string Xml = @"<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"" xmlns:flowable=""http://flowable.org/bpmn"" targetNamespace=""urn:test""><process id=""branch""><userTask id=""current""/><exclusiveGateway id=""gateway""/><userTask id=""yes"" name=""测试审批"" flowable:assignee=""${yesUser}""/><userTask id=""no"" name=""正式审批"" flowable:assignee=""${noUser}""/><sequenceFlow id=""toGateway"" sourceRef=""current"" targetRef=""gateway""/><sequenceFlow id=""trueFlow"" sourceRef=""gateway"" targetRef=""yes""><conditionExpression>${IS_TEST == true}</conditionExpression></sequenceFlow><sequenceFlow id=""falseFlow"" sourceRef=""gateway"" targetRef=""no""><conditionExpression>${IS_TEST == false}</conditionExpression></sequenceFlow></process></definitions>";
    private static List<WorkflowSlot> Slots() => new()
    {
        new() { SlotKey = "choose_yes", RoleKey = "yes_role", VariableName = "yesUser", Required = true, Mode = "single" },
        new() { SlotKey = "choose_no", RoleKey = "no_role", VariableName = "noUser", Required = true, Mode = "single" }
    };

    [Theory]
    [InlineData(true, "yes", "choose_yes", "trueFlow")]
    [InlineData(false, "no", "choose_no", "falseFlow")]
    public void ExclusiveGatewayOnlyRequiresTheChosenBranchEvenWithoutSlotConditions(bool value, string node, string slot, string flow)
    {
        var result = WorkflowNextStepResolver.Resolve(Xml, "current", new() { ["IS_TEST"] = value }, Slots());
        Assert.True(result.Resolved);
        Assert.Equal(node, Assert.Single(result.NextNodes).Id);
        Assert.Equal(slot, Assert.Single(result.Slots).SlotKey);
        Assert.Equal(new[] { "toGateway", flow }, result.FlowIds);
        Assert.Equal("IS_TEST", Assert.Single(result.Variables).Name);
        // 当前节点提交一个命中分支的人即可，不会因为另一分支 Required=true 而拒绝。
        var converter = new SlotVariableConverter(NullLogger<SlotVariableConverter>.Instance);
        var converted = converter.Convert(new() { new() { SlotKey = slot, Users = new() { "001" } } }, result.Slots);
        Assert.Equal("001", Assert.Single(converted.Variables).Value);
        Assert.ThrowsAny<Exception>(() => converter.Convert(new(), result.Slots));
    }

    [Fact]
    public void MissingBranchVariableDoesNotAskForBothPeopleOrChooseDefault()
    {
        var result = WorkflowNextStepResolver.Resolve(Xml.Replace("<exclusiveGateway id=\"gateway\"/>", "<exclusiveGateway id=\"gateway\" default=\"falseFlow\"/>"), "current", new(), Slots());
        Assert.False(result.Resolved);
        Assert.True(Assert.Single(result.Variables).Missing);
        Assert.Empty(result.Slots);
        Assert.Empty(result.NextNodes);
    }

    [Fact]
    public void DefaultIsUsedOnlyWhenEveryExplicitConditionIsFalse()
    {
        var xml = Xml.Replace("<exclusiveGateway id=\"gateway\"/>", "<exclusiveGateway id=\"gateway\" default=\"falseFlow\"/>");
        var result = WorkflowNextStepResolver.Resolve(xml, "current", new() { ["IS_TEST"] = false }, Slots());
        Assert.True(result.Resolved);
        Assert.Equal("no", Assert.Single(result.NextNodes).Id);
    }

    [Fact]
    public void ParallelGatewayStillRequiresBothBranches()
    {
        var xml = Xml.Replace("exclusiveGateway", "parallelGateway");
        var result = WorkflowNextStepResolver.Resolve(xml, "current", new(), Slots());
        Assert.True(result.Resolved);
        Assert.Equal(2, result.NextNodes.Count);
        Assert.Equal(2, result.Slots.Count);
    }

    [Fact]
    public void ComplexConditionIsReportedWithoutExecutingItOrGuessingAnotherBranch()
    {
        var result = WorkflowNextStepResolver.Resolve(Xml.Replace("IS_TEST == true", "bean.decide()"), "current", new() { ["IS_TEST"] = false }, Slots());
        Assert.False(result.Resolved);
        Assert.Contains("复杂表达式", result.Message);
        Assert.True(result.ManualConfigurationRequired);
        Assert.Empty(result.Slots);
    }

    [Fact]
    public void ExclusiveGatewayFollowsDeclarationOrderWhenSeveralConditionsMatch()
    {
        var result = WorkflowNextStepResolver.Resolve(Xml.Replace("IS_TEST == false", "IS_TEST == true"), "current", new() { ["IS_TEST"] = true }, Slots());
        Assert.True(result.Resolved);
        Assert.Equal("yes", Assert.Single(result.NextNodes).Id);
    }

    [Fact]
    public async Task UsesTheTaskBoundDefinitionAndMergesPersistedVariablesWithCurrentInput()
    {
        var repository = Proxy<IFlowableRepositoryService>((method, args) =>
        {
            Assert.Equal("GetBpmnXmlByDefinitionIdAsync", method);
            Assert.Equal("branch:3:bound", args![0]);
            return Task.FromResult(Xml);
        });
        var runtime = Proxy<IFlowableRuntimeService>((method, args) =>
        {
            Assert.Equal("GetProcessVariablesAsync", method);
            Assert.Equal("round-1", args![0]);
            return Task.FromResult(new Dictionary<string, object> { ["IS_TEST"] = false, ["persisted"] = 1 });
        });
        var service = new WorkflowNextStepAppService(repository, runtime, new SlotVariableConverter(NullLogger<SlotVariableConverter>.Instance));
        var (result, variables) = await service.ResolveAsync(new() { ProcessDefinitionId = "branch:3:bound", ProcessInstanceId = "round-1", TaskDefinitionKey = "current" }, new() { ["IS_TEST"] = true }, Slots());
        Assert.True(result.Resolved);
        Assert.Equal("choose_yes", Assert.Single(result.Slots).SlotKey);
        Assert.Equal(1, variables["persisted"]);
    }

    [Fact]
    public async Task FormalCompletionDropsUnchosenPeopleAndKeepsUnknownSlotRejection()
    {
        var repository = Proxy<IFlowableRepositoryService>((_, _) => Task.FromResult(Xml));
        var runtime = Proxy<IFlowableRuntimeService>((_, _) => Task.FromResult(new Dictionary<string, object>()));
        var slotProvider = Proxy<IProcessSlotConfigProvider>((_, _) => Task.FromResult(Slots()));
        var converter = new SlotVariableConverter(NullLogger<SlotVariableConverter>.Instance);
        var service = new TaskExecutionAppService(runtime, null!, null!, slotProvider, converter, null!,
            NullLogger<TaskExecutionAppService>.Instance, null!, null!, null!, Options.Create(new Dm8Options()),
            new WorkflowNextStepAppService(repository, runtime, converter));
        var request = new FlowableWrapper.Application.Dtos.CompleteTaskRequest
        {
            BusinessVariables = new() { ["IS_TEST"] = true },
            NextSlotSelections = new() { new() { SlotKey = "choose_yes", Users = new() { "001" } }, new() { SlotKey = "choose_no", Users = new() { "unused" } } }
        };
        var method = typeof(TaskExecutionAppService).GetMethod("BuildCompletionVariablesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<Dictionary<string, object>> Complete()
        {
            var task = (Task)method.Invoke(service, new object[] { request,
                new FlowableTask { ProcessDefinitionId = "branch:1:bound", ProcessInstanceId = "round-1", TaskDefinitionKey = "current" },
                new ProcessMetadataDocument { ProcessDefinitionKey = "branch", ProcessDefinitionVersion = 1 }, "operator" })!;
            await task;
            var tuple = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return (Dictionary<string, object>)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        }
        var values = await Complete();
        Assert.Equal("001", values["yesUser"]);
        Assert.False(values.ContainsKey("noUser"));
        request.NextSlotSelections.Add(new() { SlotKey = "not_declared", Users = new() { "002" } });
        var error = await Assert.ThrowsAnyAsync<Exception>(Complete);
        Assert.Contains("未定义", error.Message);
    }

    [Fact]
    public async Task PreviewUsesTheSameApprovalVariableAsFormalCompletion()
    {
        var repository = Proxy<IFlowableRepositoryService>((_, _) => Task.FromResult(Xml.Replace("IS_TEST", "isApproved")));
        var runtime = Proxy<IFlowableRuntimeService>((_, _) => Task.FromResult(new Dictionary<string, object>()));
        var service = new WorkflowNextStepAppService(repository, runtime, new SlotVariableConverter(NullLogger<SlotVariableConverter>.Instance));
        var (result, _) = await service.ResolveAsync(new() { ProcessDefinitionId = "branch:1:bound", ProcessInstanceId = "round-1", TaskDefinitionKey = "current" }, new() { ["isApproved"] = false }, Slots());
        Assert.True(result.Resolved);
        Assert.Equal("yes", Assert.Single(result.NextNodes).Id);
    }

    private static T Proxy<T>(Func<string, object?[]?, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, Handler>();
        ((Handler)(object)value).Execute = handler;
        return value;
    }
    public class Handler : DispatchProxy
    {
        public Func<string, object?[]?, object?> Execute { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Execute(method!.Name, args);
    }
}
