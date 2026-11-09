using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Reliability;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class WorkflowWorkbenchReadinessTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, "flow:4:other", false)]
    [InlineData(true, "flow:4:current", true)]
    public async Task ExplainsActiveGateWithoutWritingOrStarting(bool exists, string? configuredId, bool canStart)
    {
        var repository = System.Reflection.DispatchProxy.Create<IFlowableRepositoryService, DefinitionStartabilityTests.ReadOnlyProxy>();
        ((DefinitionStartabilityTests.ReadOnlyProxy)(object)repository).Handle = (method, args) =>
        {
            Assert.Equal("GetLatestProcessDefinitionByKeyAsync", method.Name);
            Assert.Equal("flow", args![0]);
            return Task.FromResult(exists ? new FlowableProcessDefinition { Id = "flow:4:current", Key = "flow", Version = 4 } : null);
        };
        var store = System.Reflection.DispatchProxy.Create<IWorkflowReliabilityStore, DefinitionStartabilityTests.ReadOnlyProxy>();
        ((DefinitionStartabilityTests.ReadOnlyProxy)(object)store).Handle = (method, args) =>
        {
            Assert.True(exists);
            Assert.Equal("GetDefinitionConfigAsync", method.Name);
            Assert.Equal(4, args![1]);
            return Task.FromResult(configuredId == null ? null : new WorkflowDefinitionConfig { ProcessDefinitionId = configuredId });
        };
        var result = await new WorkflowWorkbenchAppService(repository, store, null!).GetReadinessAsync("flow");
        Assert.Equal(canStart, result.CanStart);
        Assert.Equal(exists ? 3 : 1, result.Checks.Count);
        Assert.All(result.Checks.Where(check => !check.Passed), check =>
        {
            Assert.Equal("publish", check.Action);
            Assert.False(string.IsNullOrWhiteSpace(check.Detail));
        });
    }
}
