using System.Reflection;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Configuration;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class StarterVariableTests
{
    [Fact]
    public void StarterAssignmentUsesAuthenticatedIdentityAndPreservesSlotArrays()
    {
        var service = new ProcessLifecycleAppService(null!, null!, null!, null!, null!, null!, null!, null!,
            Options.Create(new BusinessTypeProcessMapping()), Options.Create(new FlowableOptions()),
            NullLogger<ProcessLifecycleAppService>.Instance, null!, null!, Options.Create(new Dm8Options()));
        var request = new FlowableWrapper.Application.Dtos.StartProcessRequest
        {
            BusinessId = "version-1",
            BusinessVariables = new Dictionary<string, object> { ["starterAssignee"] = "untrusted-user" }
        };
        var assignees = new List<string> { "user-1", "user-2" };
        var method = typeof(ProcessLifecycleAppService).GetMethod("BuildStartVariables", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var variables = (Dictionary<string, object>)method.Invoke(service,
            new object[] { request, "lab", new Dictionary<string, object> { ["firstApprovers"] = assignees }, "authenticated-user" })!;
        Assert.Equal("authenticated-user", variables["starterAssignee"]);
        Assert.Equal("version-1", variables["businessId"]);
        Assert.Same(assignees, variables["firstApprovers"]);
    }
}
