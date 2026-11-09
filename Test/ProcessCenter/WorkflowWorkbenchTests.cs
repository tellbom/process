using System.Reflection;
using System.Text.Json;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Slots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class WorkflowWorkbenchTests
{
    private const string Xml = @"
        <definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"" xmlns:flowable=""http://flowable.org/bpmn"" targetNamespace=""urn:test"">
          <process id=""workbench_test"" name=""测试"" isExecutable=""true"">
            <startEvent id=""start""/><userTask id=""review"" flowable:assignee=""${starterAssignee}""/><endEvent id=""end""/>
            <sequenceFlow id=""a"" sourceRef=""start"" targetRef=""review""/><sequenceFlow id=""b"" sourceRef=""review"" targetRef=""end""/>
          </process>
        </definitions>
        ";
    private const string Config = @"[{""taskDefinitionKey"":""review"",""isStarterNode"":true,""roleKey"":""reviewer"",""slots"":[],""customExtension"":{""preserved"":true}}]";

    [Fact]
    public void CompiledResourceCarriesConfigurationAndPreservesUnrecognizedJson()
    {
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = Config });
        Assert.True(result.Valid, string.Join(";", result.Issues.Select(x => x.Message)));
        var embedded = WorkflowArtifactCompiler.ReadEmbeddedConfiguration(WorkflowArtifactCompiler.Parse(result.BpmnXml));
        Assert.Equal(result.SlotConfigJson, embedded);
        using var json = JsonDocument.Parse(embedded!);
        Assert.True(json.RootElement[0].GetProperty("customExtension").GetProperty("preserved").GetBoolean());
        Assert.False(json.RootElement[0].TryGetProperty("callbackUrl", out _));
        var second = WorkflowArtifactCompiler.Compile(new() { BpmnXml = result.BpmnXml });
        Assert.Equal(result.BpmnXml, second.BpmnXml);
    }

    [Fact]
    public void ExplicitEmptyCallbackSurvivesRoundTripAndSidecarOverridesEmbedded()
    {
        var original = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = Config });
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = original.BpmnXml, SlotConfigJson = @"[{""taskDefinitionKey"":""review"",""callbackUrl"":null}]" });
        using var json = JsonDocument.Parse(result.SlotConfigJson);
        Assert.Equal(JsonValueKind.Null, json.RootElement[0].GetProperty("callbackUrl").ValueKind);
        Assert.Single(WorkflowArtifactCompiler.Parse(result.BpmnXml).Descendants(WorkflowArtifactCompiler.Configuration + "nodeConfigurations"));
    }

    [Theory]
    [InlineData("[{\"taskDefinitionKey\":\"missing\"}]", "UNKNOWN_TASK")]
    [InlineData("[{\"taskDefinitionKey\":\"review\"},{\"taskDefinitionKey\":\"review\"}]", "DUPLICATE_CONFIGURATION")]
    [InlineData("[null]", "INVALID_CONFIGURATION")]
    [InlineData("{}", "INVALID_CONFIGURATION")]
    [InlineData("[{\"taskDefinitionKey\":\"review\",\"canReject\":true}]", "REJECT_OPTIONS_REQUIRED")]
    [InlineData("[{\"taskDefinitionKey\":\"review\",\"isRejectTarget\":true}]", "INVALID_REJECT_TARGET")]
    public void InvalidConfigurationReturnsNodeIssues(string json, string code)
    {
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = json });
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, x => x.Code == code && x.Severity == "error");
    }

    [Fact]
    public void DanglingFlowAndDuplicateXmlIdsCannotBeDeployed()
    {
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml.Replace("targetRef=\"review\"", "targetRef=\"absent\"").Replace("id=\"end\"", "id=\"review\""), SlotConfigJson = Config });
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, x => x.Code == "DUPLICATE_ID");
        Assert.Contains(result.Issues, x => x.Code == "INVALID_FLOW_REFERENCE");
    }

    [Fact]
    public void DtdAndExternalEntityAreRejected()
    {
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = "<!DOCTYPE definitions [<!ENTITY x SYSTEM 'file:///unreadable'>]>" + Xml });
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, x => x.Code == "INVALID_XML");
    }

    [Fact]
    public void SingleUserSlotCannotFeedAMultiInstanceCollection()
    {
        var xml = Xml.Replace("flowable:assignee=\"${starterAssignee}\"/>", "flowable:assignee=\"${item.asText()}\"><multiInstanceLoopCharacteristics flowable:collection=\"${reviewers}\" flowable:elementVariable=\"item\"/></userTask>");
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = xml, SlotConfigJson = @"[{""taskDefinitionKey"":""review"",""slots"":[{""slotKey"":""next"",""roleKey"":""reviewer"",""variableName"":""reviewers"",""mode"":""single""}]}]" });
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "SLOT_COLLECTION_MODE_MISMATCH");
    }

    [Fact]
    public async Task EmbeddedConfigurationIsReadFromExactFlowableDefinitionWithoutConsultingProjection()
    {
        var artifact = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = Config });
        var repository = Proxy<IFlowableRepositoryService>((method, args) => method switch
        {
            "GetProcessDefinitionByKeyAndVersionAsync" => Task.FromResult(new FlowableProcessDefinition { Id = "workbench_test:12:engine", Key = "workbench_test", Version = 12 }),
            "GetBpmnXmlByDefinitionIdAsync" => args![0]!.Equals("workbench_test:12:engine") ? Task.FromResult(artifact.BpmnXml) : throw new Exception("Wrong engine definition"),
            _ => throw new Exception("Unexpected query: " + method)
        });
        var store = Proxy<IWorkflowReliabilityStore>((_, _) => throw new Exception("Projection must not become truth"));
        var es = Proxy<IElasticSearchService>((_, _) => throw new Exception("ES must not become truth"));
        var deployment = new BpmnDeploymentAppService(repository, es, store, null!, null!, Options.Create(new Dm8Options { Enabled = true }), NullLogger<BpmnDeploymentAppService>.Instance);
        var provider = new Dm8ProcessSlotConfigProvider(store, es, Options.Create(new Dm8Options { Enabled = true }), NullLogger<Dm8ProcessSlotConfigProvider>.Instance, repository, deployment);
        var map = await provider.GetNodeSemanticMapAsync("workbench_test", 12);
        Assert.Equal("reviewer", map["review"].RoleKey);
        Assert.True(map["review"].IsStarterNode);
    }

    [Fact]
    public async Task WorkbenchAssetReadsEngineResourceWithoutProjection()
    {
        var artifact = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = Config });
        var repository = Proxy<IFlowableRepositoryService>((method, _) => method switch
        {
            "GetLatestProcessDefinitionByKeyAsync" => Task.FromResult(new FlowableProcessDefinition { Id = "workbench_test:13:engine", Key = "workbench_test", Version = 13 }),
            "GetBpmnXmlByDefinitionIdAsync" => Task.FromResult(artifact.BpmnXml),
            _ => throw new Exception("Unexpected query")
        });
        var service = new WorkflowWorkbenchAppService(repository, null!, null!);
        var asset = await service.GetArtifactAsync("workbench_test");
        Assert.Equal("flowable", asset.ConfigurationSource);
        Assert.Equal(artifact.SlotConfigJson, asset.SlotConfigJson);
    }

    [Fact]
    public void ExistingPairedProcessFilesPassCrossArtifactValidation()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../bpmn"));
        foreach (var file in Directory.EnumerateFiles(root, "*.bpmn", SearchOption.AllDirectories))
        {
            var json = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "_slot.json");
            var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = File.ReadAllText(file), SlotConfigJson = File.Exists(json) ? File.ReadAllText(json) : null });
            Assert.True(result.Valid, Path.GetFileName(file) + ": " + string.Join("; ", result.Issues.Where(x => x.Severity == "error").Select(x => x.ElementId + " " + x.Message)));
        }
    }

    [Fact]
    public async Task RunnerReadsTheTasksExactDefinitionAfterOverwrite()
    {
        var config = Config.Replace("\"customExtension\"", "\"inputHints\":[{\"name\":\"decision\",\"stage\":\"complete\",\"description\":\"true / false\"}],\"customExtension\"");
        var artifact = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = config });
        var repository = Proxy<IFlowableRepositoryService>((method, args) => method switch
        {
            "GetProcessDefinitionByKeyAndVersionAsync" when args![0]!.Equals("workbench_test") && args[1]!.Equals(4)
                => Task.FromResult(new FlowableProcessDefinition { Id = "workbench_test:4:engine", Key = "workbench_test", Version = 4 }),
            "GetBpmnXmlByDefinitionIdAsync" when args![0]!.Equals("workbench_test:4:engine") => Task.FromResult(artifact.BpmnXml),
            _ => throw new Exception("Runner must not read the latest definition or projection")
        });
        var service = new WorkflowWorkbenchAppService(repository, null!, null!);
        var result = await service.GetArtifactAsync("workbench_test", 4);
        Assert.Equal(4, result.Definition.Version);
        using var json = JsonDocument.Parse(result.SlotConfigJson);
        Assert.Equal("decision", json.RootElement[0].GetProperty("inputHints")[0].GetProperty("name").GetString());
        await Assert.ThrowsAsync<BusinessException>(() => service.GetArtifactAsync("workbench_test", 0));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"name\":\"\",\"stage\":\"complete\"}]")]
    [InlineData("[{\"name\":\"flag\",\"stage\":\"unknown\"}]")]
    public void InvalidInputHintsCannotBePublished(string hints)
    {
        var config = Config.Replace("\"customExtension\"", "\"inputHints\":" + hints + ",\"customExtension\"");
        var result = WorkflowArtifactCompiler.Compile(new() { BpmnXml = Xml, SlotConfigJson = config });
        Assert.Contains(result.Issues, issue => issue.Code == "INVALID_INPUT_HINT");
        Assert.False(result.Valid);
    }

    [Fact]
    public async Task CallbackResolvesTheInstancesEngineVersionAfterOverwrite()
    {
        object? observedVersion = null;
        var slots = Proxy<IProcessSlotConfigProvider>((method, args) =>
        {
            observedVersion = args![1];
            return Task.FromResult(new Dictionary<string, NodeSemanticInfo>
            {
                ["review"] = new() { CallbackUrlSpecified = true, CallbackUrl = null! }
            });
        });
        var service = new ProcessCallbackAppService(null!, null!, slots, null!, Options.Create(new Dm8Options()), NullLogger<ProcessCallbackAppService>.Instance);
        var method = typeof(ProcessCallbackAppService).GetMethod("ResolveNodeCallbackUrlAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var url = await (Task<string?>)method.Invoke(service, new object?[] { "review", "workbench_test", "https://business.invalid/callback", 7 })!;
        Assert.Equal(7, observedVersion);
        Assert.Null(url);
    }

    [Fact]
    public async Task CallbackDoesNotGuessAnotherTargetWhenBoundConfigurationCannotBeRead()
    {
        var slots = Proxy<IProcessSlotConfigProvider>((_, _) => throw new InvalidOperationException("engine unavailable"));
        var service = new ProcessCallbackAppService(null!, null!, slots, null!, Options.Create(new Dm8Options()), NullLogger<ProcessCallbackAppService>.Instance);
        var method = typeof(ProcessCallbackAppService).GetMethod("ResolveNodeCallbackUrlAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var url = await (Task<string?>)method.Invoke(service, new object?[] { "review", "workbench_test", "https://business.invalid/callback", 7 })!;
        Assert.Null(url);
    }

    [Fact]
    public async Task CallbackAuditIsScopedToTheSameInstance()
    {
        object? observedInstance = null;
        var es = Proxy<IElasticSearchService>((method, args) =>
        {
            observedInstance = args![1];
            return Task.FromResult(new List<ProcessAuditRecord>());
        });
        var service = new ProcessCallbackAppService(es, null!, null!, null!, Options.Create(new Dm8Options()), NullLogger<ProcessCallbackAppService>.Instance);
        var method = typeof(ProcessCallbackAppService).GetMethod("BuildNodeCallbackContextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, new object?[] { "old-round", "business", "workbench_test", "review", new ProcessMetadataDocument { ProcessDefinitionKey = "workbench_test" } })!;
        Assert.Equal("old-round", observedInstance);
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
