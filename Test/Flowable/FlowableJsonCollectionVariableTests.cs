using System.Net;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public sealed class FlowableJsonCollectionVariableTests
{
    [Fact]
    public async Task StartAndComplete_SendAssigneeListsAsJsonArrays()
    {
        var requests = new List<(string Path, string Body)>();
        var handler = new RecordingHandler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"process-1\",\"processDefinitionId\":\"test:1:1\",\"businessKey\":\"business-1\",\"ended\":false}",
                    Encoding.UTF8, "application/json")
            };
        });
        var options = Options.Create(new FlowableOptions
        {
            BaseUrl = "http://flowable.test/flowable-rest/service",
            Username = "test",
            Password = "test",
            TimeoutSeconds = 5
        });
        var runtime = new FlowableRuntimeServiceImpl(
            new HttpClient(handler), options,
            NullLogger<FlowableRuntimeServiceImpl>.Instance);
        var task = new FlowableTaskServiceImpl(
            new FlowableHttpClient(new HttpClient(handler), options,
                NullLogger<FlowableHttpClient>.Instance),
            NullLogger<FlowableTaskServiceImpl>.Instance);

        using var document = JsonDocument.Parse("[\"196045\"]");
        foreach (object users in new object[]
                 {
                     new List<string> { "196045" }, document.RootElement.Clone()
                 })
        {
            var variables = new Dictionary<string, object>
            {
                ["starterAssignee"] = "196045",
                ["reviewApproverAssigneeList"] = users
            };
            await runtime.StartProcessInstanceByKeyAsync(
                "test", "business-1", variables);
            await task.CompleteAsync("task-1", variables);
        }

        Assert.Equal(4, requests.Count);
        foreach (var request in requests)
        {
            using var body = JsonDocument.Parse(request.Body);
            var variables = body.RootElement.GetProperty("variables");
            var starter = variables.EnumerateArray().Single(variable =>
                variable.GetProperty("name").GetString() == "starterAssignee");
            Assert.Equal("string", starter.GetProperty("type").GetString());
            Assert.Equal("196045", starter.GetProperty("value").GetString());

            var approvers = variables.EnumerateArray().Single(variable =>
                variable.GetProperty("name").GetString() == "reviewApproverAssigneeList");
            Assert.Equal("json", approvers.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Array,
                approvers.GetProperty("value").ValueKind);
            Assert.Equal("196045", approvers.GetProperty("value")[0].GetString());
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handle;

        public RecordingHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        {
            _handle = handle;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handle(request);
    }
}
