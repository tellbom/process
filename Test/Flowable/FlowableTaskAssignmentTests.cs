using System.Net;
using System.Text.Json;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public sealed class FlowableTaskAssignmentTests
{
    [Theory]
    [InlineData("196045")]
    [InlineData(null)]
    public async Task SetAssignee_UpdatesTaskWithPut(string? assignee)
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var handler = new RecordingHandler(async request =>
        {
            method = request.Method;
            path = request.RequestUri!.AbsolutePath;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var options = Options.Create(new FlowableOptions
        {
            BaseUrl = "http://flowable.test/flowable-rest/service",
            Username = "test",
            Password = "test",
            TimeoutSeconds = 5
        });
        var service = new FlowableTaskServiceImpl(
            new FlowableHttpClient(new HttpClient(handler), options,
                NullLogger<FlowableHttpClient>.Instance),
            NullLogger<FlowableTaskServiceImpl>.Instance);

        await service.SetAssigneeAsync("task-1", assignee);

        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/flowable-rest/service/runtime/tasks/task-1", path);
        using var json = JsonDocument.Parse(body!);
        Assert.Single(json.RootElement.EnumerateObject());
        var actualAssignee = json.RootElement.GetProperty("assignee");
        if (assignee is null)
            Assert.Equal(JsonValueKind.Null, actualAssignee.ValueKind);
        else
            Assert.Equal(assignee, actualAssignee.GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handle;

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
            => _handle = handle;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handle(request);
    }
}
