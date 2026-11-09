using System.Reflection;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class DefinitionStartabilityTests
{
    [Theory]
    [InlineData(false, null, "PROCESS_DEFINITION_NOT_FOUND")]
    [InlineData(true, null, "PROCESS_DEFINITION_NOT_ACTIVE")]
    [InlineData(true, "lab:8:old", "PROCESS_DEFINITION_NOT_ACTIVE")]
    public async Task MissingOrMismatchedConfigCannotPassGate(bool exists, string? configuredId, string errorCode)
    {
        var service = Service(exists, configuredId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.GetStartabilityAsync("lab"));
        Assert.Equal(errorCode, error.Code);
    }

    [Fact]
    public async Task MatchingLatestDefinitionPassesWithoutWritingAnything()
    {
        var result = await Service(true, "lab:9:current").GetStartabilityAsync("lab");
        Assert.True(result.CanStart);
        Assert.Equal("lab", result.ProcessDefinitionKey);
        Assert.Equal("lab:9:current", result.ProcessDefinitionId);
        Assert.Equal(9, result.Version);
    }

    private static BpmnDeploymentAppService Service(bool exists, string? configuredId)
    {
        var repository = DispatchProxy.Create<IFlowableRepositoryService, ReadOnlyProxy>();
        ((ReadOnlyProxy)(object)repository).Handle = (method, args) =>
        {
            Assert.Equal("GetLatestProcessDefinitionByKeyAsync", method.Name);
            Assert.Equal("lab", args![0]);
            return Task.FromResult(exists ? new FlowableProcessDefinition { Id = "lab:9:current", Key = "lab", Version = 9 } : null);
        };
        var store = DispatchProxy.Create<IWorkflowReliabilityStore, ReadOnlyProxy>();
        ((ReadOnlyProxy)(object)store).Handle = (method, args) =>
        {
            Assert.Equal("GetDefinitionConfigAsync", method.Name);
            Assert.Equal("lab", args![0]);
            Assert.Equal(9, args[1]);
            return Task.FromResult(configuredId == null ? null : new WorkflowDefinitionConfig { ProcessDefinitionId = configuredId });
        };
        return new BpmnDeploymentAppService(repository, null!, store, null!, null!,
            Options.Create(new Dm8Options { Enabled = true }), NullLogger<BpmnDeploymentAppService>.Instance);
    }

    public class ReadOnlyProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handle { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handle(method!, args);
    }
}
