using System.Reflection;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class ProcessOperationsTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task ActionsRequireCurrentRoundAndAnActiveEngineInstance(bool latest, bool active, bool allowed)
    {
        var es = Es(latest);
        var runtime = Proxy<IFlowableRuntimeService>((method, args) => Task.FromResult(active ? new FlowableProcessInstance { Id = "selected" } : null));
        var tasks = Proxy<IFlowableTaskService>((method, args) => method switch
        {
            "QueryTaskPageAsync" => Task.FromResult(new FlowableTaskPage { Items = new() { new FlowableTask { Id = "task-1", TaskDefinitionKey = "review", ProcessInstanceId = "selected" } }, Total = 1, Size = 1 }),
            "GetCandidateUsersAsync" => Task.FromResult(new List<string> { "candidate" }),
            _ => throw new Exception("Unexpected method: " + method)
        });
        var slots = Proxy<IProcessSlotConfigProvider>((method, args) =>
        {
            Assert.Equal(4, args![1]);
            return Task.FromResult(new Dictionary<string, NodeSemanticInfo> { ["review"] = new() { CanReassign = true } });
        });
        var service = new ProcessOperationsAppService(es, null!, runtime, tasks, slots, Options.Create(new Dm8Options()), NullLogger<ProcessOperationsAppService>.Instance);
        var result = await service.GetAsync("business", "selected");
        Assert.Equal(allowed, result.AvailableActions.Contains("terminate"));
        Assert.Equal(latest, result.IsLatestRound);
        Assert.Equal(active, result.IsActive);
        if (allowed) Assert.True(Assert.Single(result.Tasks).CanReassign);
        else { Assert.Empty(result.Tasks); Assert.NotEmpty(result.UnavailableReason!); }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    public async Task EngineMissingIsInactiveButUnavailableEngineIsAnError(int status)
    {
        var runtime = Proxy<IFlowableRuntimeService>((method, args) => throw new FlowableApiException("engine", status));
        if (status == 404) Assert.False(await ProcessOperationsAppService.GetActiveAsync(runtime, "selected"));
        else await Assert.ThrowsAsync<FlowableApiException>(() => ProcessOperationsAppService.GetActiveAsync(runtime, "selected"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminationUsesTheSelectedInstanceAndPreservesTheReason(bool active)
    {
        var calls = new List<string>();
        var es = Proxy<IElasticSearchService>((method, args) => method switch
        {
            "GetProcessMetadataByBusinessIdAsync" => Task.FromResult(Metadata("selected")),
            "UpdateProcessStatusAsync" => CaptureUpdate(args, calls),
            _ => throw new Exception(method)
        });
        var runtime = Proxy<IFlowableRuntimeService>((method, args) =>
        {
            Assert.Equal("selected", args![0]);
            if (method == "GetProcessInstanceAsync") return Task.FromResult(active ? new FlowableProcessInstance { Id = "selected" } : null);
            Assert.Equal("DeleteProcessInstanceAsync", method);
            Assert.Equal("运维收回测试原因", args[1]);
            calls.Add("delete");
            return Task.CompletedTask;
        });
        var service = new ProcessLifecycleAppService(runtime, null!, null!, es, null!, null!, null!, null!,
            Options.Create(new BusinessTypeProcessMapping()), Options.Create(new FlowableOptions()),
            NullLogger<ProcessLifecycleAppService>.Instance, null!, null!, Options.Create(new Dm8Options()));
        var request = new TerminateProcessRequest { BusinessId = "business", ProcessInstanceId = "selected", Reason = "运维收回测试原因" };
        if (active)
        {
            await service.TerminateProcessAsync(request);
            Assert.Equal(new[] { "delete", "update" }, calls);
        }
        else
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() => service.TerminateProcessAsync(request));
            Assert.Equal("PROCESS_NOT_ACTIVE", error.Code);
            Assert.Empty(calls);
        }
    }

    [Fact]
    public async Task AnotherBusinessesInstanceIsRejectedBeforeEngineLookup()
    {
        var service = new ProcessOperationsAppService(Es(true), null!, null!, null!, null!, Options.Create(new Dm8Options()), NullLogger<ProcessOperationsAppService>.Instance);
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.GetAsync("wrong", "selected"));
        Assert.Equal("PROCESS_INSTANCE_NOT_FOUND", error.Code);
    }

    private static Task CaptureUpdate(object?[]? args, List<string> calls)
    {
        Assert.Equal("selected", args![0]); Assert.Equal("terminated", args[1]);
        calls.Add("update"); return Task.CompletedTask;
    }

    [Fact]
    public async Task BrokenNodeConfigurationDoesNotPreventAdministrativeTermination()
    {
        var runtime = Proxy<IFlowableRuntimeService>((method, args) => Task.FromResult(new FlowableProcessInstance { Id = "selected" }));
        var tasks = Proxy<IFlowableTaskService>((method, args) => Task.FromResult(new FlowableTaskPage { Total = 0 }));
        var slots = Proxy<IProcessSlotConfigProvider>((method, args) => throw new InvalidOperationException("configuration unavailable"));
        var service = new ProcessOperationsAppService(Es(true), null!, runtime, tasks, slots, Options.Create(new Dm8Options()), NullLogger<ProcessOperationsAppService>.Instance);
        var result = await service.GetAsync("business", "selected");
        Assert.Contains("terminate", result.AvailableActions);
        Assert.NotEmpty(result.TaskConfigurationWarning!);
    }
    private static ProcessMetadataDocument Metadata(string id) => new() { BusinessId = "business", ProcessInstanceId = id, ProcessDefinitionKey = "definition", ProcessDefinitionVersion = 4, Status = "running" };
    private static IElasticSearchService Es(bool latest) => Proxy<IElasticSearchService>((method, args) => method switch
    {
        "GetProcessMetadataAsync" => Task.FromResult(Metadata("selected")),
        "QueryProcessListAsync" => Task.FromResult((new List<ProcessMetadataDocument> { Metadata(latest ? "selected" : "newer") }, 1)),
        _ => throw new Exception(method)
    });
    private static T Proxy<T>(Func<string, object?[]?, object?> action) where T : class
    {
        var proxy = DispatchProxy.Create<T, ReadProxy>(); ((ReadProxy)(object)proxy).Action = action; return proxy;
    }
    public class ReadProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Action { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!.Name, args);
    }
}
