using System.Net;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public sealed class FlowableHistoryPaginationTests
{
    [Fact]
    public async Task ReadsPast200TasksWithTheSameInstanceFilter()
    {
        var paths = new List<string>();
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            var start = path.Contains("start=200") ? 200 : 0;
            var count = start == 0 ? 200 : 1;
            return JsonSerializer.Serialize(new
            {
                total = 201,
                data = Enumerable.Range(start, count).Select(i => new
                {
                    id = $"task-{i}", processInstanceId = "instance-1", startTime = "2026-10-14T01:00:00Z"
                })
            });
        });
        var result = await CreateService(handler).QueryHistoricTasksAsync(new FlowableHistoricTaskQuery
        {
            ProcessInstanceId = "instance-1"
        });
        Assert.Equal(201, result.Count);
        Assert.Equal(2, paths.Count);
        Assert.All(paths, path =>
        {
            Assert.Contains("processInstanceId=instance-1", path);
            Assert.Contains("sort=taskInstanceId", path);
        });
        Assert.Contains("start=200", paths[1]);
        Assert.Equal("task-200", result[^1].Id);
    }

    [Fact]
    public async Task AnIncompletePageFailsInsteadOfReturningTruncatedHistory()
    {
        var handler = new Handler(_ => "{\"total\":1,\"data\":[]}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(handler).QueryHistoricTasksAsync(new FlowableHistoricTaskQuery { ProcessInstanceId = "instance-1" }));
    }

    private static FlowableHistoryServiceImpl CreateService(HttpMessageHandler handler)
        => new(new FlowableHttpClient(new HttpClient(handler), Options.Create(new FlowableOptions
        {
            BaseUrl = "http://flowable.test/flowable-rest/service", Username = "test", Password = "test", TimeoutSeconds = 5
        }), NullLogger<FlowableHttpClient>.Instance), NullLogger<FlowableHistoryServiceImpl>.Instance);

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _response;
        public Handler(Func<HttpRequestMessage, string> response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response(request), Encoding.UTF8, "application/json")
            });
    }
}
