using System.Net;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public class FlowableRepositoryResourceTests
{
    [Theory]
    [InlineData("list", false)]
    [InlineData("latest", false)]
    [InlineData("version", false)]
    [InlineData("list", true)]
    [InlineData("latest", true)]
    [InlineData("version", true)]
    public async Task DefinitionQueriesReadResourceFilenameFromFlowableResponse(string query, bool explicitName)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/repository/process-definitions", request.RequestUri!.AbsolutePath);
            Assert.Contains(query == "version" ? "version=2" : "latest=true", request.RequestUri.Query);
            var definition = new Dictionary<string, object>
            {
                ["id"] = "approval:2:definition", ["key"] = "approval", ["name"] = "审批",
                ["version"] = 2, ["deploymentId"] = "deployment",
                ["resource"] = "http://flowable.test/repository/deployments/deployment/resources/" + Uri.EscapeDataString("流程 approval.bpmn20.xml")
            };
            if (explicitName) definition["resourceName"] = "explicit.bpmn";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { definition }, total = 1 }), Encoding.UTF8, "application/json")
            };
        }));
        var http = new FlowableHttpClient(client, Options.Create(new FlowableOptions { BaseUrl = "http://flowable.test", TimeoutSeconds = 5 }), NullLogger<FlowableHttpClient>.Instance);
        var service = new FlowableRepositoryServiceImpl(http, NullLogger<FlowableRepositoryServiceImpl>.Instance);
        var result = query switch
        {
            "list" => Assert.Single((await service.QueryLatestProcessDefinitionsAsync(null, 0, 20)).Items),
            "latest" => await service.GetLatestProcessDefinitionByKeyAsync("approval"),
            _ => await service.GetProcessDefinitionByKeyAndVersionAsync("approval", 2)
        };
        Assert.Equal(explicitName ? "explicit.bpmn" : "流程 approval.bpmn20.xml", result.ResourceName);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(_send(request));
    }
}
