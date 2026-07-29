using System.Net;
using System.Text;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Infrastructure.Flowable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Flowable;

public sealed class FlowableTaskPagingTests
{
    [Fact]
    public async Task QueryTaskPage_UsesInvolvedUserAndParsesPagingEnvelope()
    {
        string? requestUri = null;
        var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{
                      ""data"": [{
                        ""id"": ""task-1"",
                        ""name"": ""审批"",
                        ""processInstanceId"": ""process-1"",
                        ""processDefinitionId"": ""portal:1:definition"",
                        ""taskDefinitionKey"": ""approve"",
                        ""createTime"": ""2026-07-29T08:00:00Z"",
                        ""priority"": 50
                      }],
                      ""total"": 1000,
                      ""start"": 120,
                      ""size"": 1
                    }",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var httpClient = new FlowableHttpClient(
            new HttpClient(handler),
            Options.Create(new FlowableOptions
            {
                BaseUrl = "http://flowable.test/flowable-rest/service",
                Username = "test",
                Password = "test",
                TimeoutSeconds = 5
            }),
            NullLogger<FlowableHttpClient>.Instance);
        var service = new FlowableTaskServiceImpl(
            httpClient,
            NullLogger<FlowableTaskServiceImpl>.Instance);

        var page = await service.QueryTaskPageAsync(new FlowableTaskQuery
        {
            InvolvedUser = "user 01",
            Start = 120,
            Size = 20,
            Sort = "createTime",
            Order = "desc"
        });

        Assert.Equal(1000, page.Total);
        Assert.Equal(120, page.Start);
        Assert.Single(page.Items);
        Assert.Contains("involvedUser=user%2001", requestUri);
        Assert.Contains("start=120", requestUri);
        Assert.Contains("size=20", requestUri);
        Assert.Contains("sort=createTime", requestUri);
        Assert.Contains("order=desc", requestUri);
    }

    [Fact]
    public async Task QueryTaskPage_ClampsSizeAndNormalizesSort()
    {
        string? requestUri = null;
        var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"total\":0,\"start\":0,\"size\":0}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new FlowableHttpClient(
            new HttpClient(handler),
            Options.Create(new FlowableOptions
            {
                BaseUrl = "http://flowable.test",
                TimeoutSeconds = 5
            }),
            NullLogger<FlowableHttpClient>.Instance);
        var service = new FlowableTaskServiceImpl(
            client,
            NullLogger<FlowableTaskServiceImpl>.Instance);

        await service.QueryTaskPageAsync(new FlowableTaskQuery
        {
            Start = -9,
            Size = 9999,
            Sort = "unsafe",
            Order = "unsafe"
        });

        Assert.Contains("start=0", requestUri);
        Assert.Contains("size=100", requestUri);
        Assert.Contains("sort=createTime", requestUri);
        Assert.Contains("order=desc", requestUri);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            _handle = handle;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handle(request));
    }
}
