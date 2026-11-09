using System.Net;
using System.Text;
using System.Text.Json;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public class FlowableHistoryPagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsAllHistoricTasksAndRejectsPrematureEmptyPage(bool incomplete)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Contains("sort=taskInstanceId", request.RequestUri!.Query);
            var offset = calls++ == 0 ? 0 : 200;
            Assert.Contains("start=" + offset, request.RequestUri.Query);
            var data = Enumerable.Range(offset, offset == 0 ? 200 : incomplete ? 0 : 1)
                .Select(i => new { id = "task-" + i, processInstanceId = "round-1" }).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data, total = 201 }), Encoding.UTF8, "application/json")
            };
        }));
        var http = new FlowableHttpClient(client, Options.Create(new FlowableOptions { BaseUrl = "http://flowable.test", TimeoutSeconds = 5 }), NullLogger<FlowableHttpClient>.Instance);
        var service = new FlowableHistoryServiceImpl(http, NullLogger<FlowableHistoryServiceImpl>.Instance);
        if (incomplete) await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryHistoricTasksAsync(new FlowableHistoricTaskQuery { ProcessInstanceId = "round-1" }));
        else Assert.Equal(201, (await service.QueryHistoricTasksAsync(new FlowableHistoricTaskQuery { ProcessInstanceId = "round-1" })).Count);
        Assert.Equal(2, calls);
    }
    private class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(_send(request));
    }
}
