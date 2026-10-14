using System.Text;
using System.Text.Json;
using Elasticsearch.Net;
using FlowableWrapper.Infrastructure.ElasticSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nest;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class ApprovalAuditQueryTests
{
    [Fact]
    public async Task UsesAnInstanceFilterAndSearchAfterBeyond200Records()
    {
        var connection = new ResponsesConnection(new[] { (Page(0, 200), 200), (Page(200, 1), 200) });
        var bodies = new List<string>();
        var service = CreateService(connection, bodies);
        var records = await service.QueryAuditRecordsByProcessInstanceIdAsync("instance-1");

        Assert.Equal(201, records.Count);
        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("instance-1", json.RootElement.GetProperty("query").GetProperty("term")
                .GetProperty("processInstanceId.keyword").GetProperty("value").GetString());
        }
        using var nextPage = JsonDocument.Parse(bodies[1]);
        Assert.Equal("audit-199", nextPage.RootElement.GetProperty("search_after")[1].GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackendFailureDoesNotBecomeNotFoundOrEmptyHistory(bool metadataQuery)
    {
        var connection = new ResponsesConnection(new[]
        {
            ("{\"error\":{\"type\":\"unavailable\",\"reason\":\"backend unavailable\"},\"status\":500}", 500)
        });
        var service = CreateService(connection, new());
        if (metadataQuery)
            await Assert.ThrowsAsync<Exception>(() => service.GetProcessMetadataAsync("instance-1"));
        else
            await Assert.ThrowsAsync<Exception>(() => service.QueryAuditRecordsByProcessInstanceIdAsync("instance-1"));
    }

    [Fact]
    public async Task AnActuallyMissingInstanceReturnsNull()
    {
        var service = CreateService(new ResponsesConnection(new[]
        {
            ("{\"_index\":\"flowable-process-metadata\",\"_id\":\"missing\",\"found\":false}", 404)
        }), new());
        Assert.Null(await service.GetProcessMetadataAsync("missing"));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task APartialSearchDoesNotReturnEmptySuccess(bool timedOut, int failedShards)
    {
        var body = JsonSerializer.Serialize(new
        {
            took = 1, timed_out = timedOut,
            _shards = new { total = 1, successful = 1 - failedShards, skipped = 0, failed = failedShards },
            hits = new { total = new { value = 0, relation = "eq" }, hits = Array.Empty<object>() }
        });
        var service = CreateService(new ResponsesConnection(new[] { (body, 200) }), new());
        await Assert.ThrowsAsync<Exception>(() => service.QueryAuditRecordsByProcessInstanceIdAsync("instance-1"));
    }

    private static ElasticSearchServiceImpl CreateService(IConnection connection, List<string> bodies)
    {
        var settings = new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://es.test")), connection)
            .DisableDirectStreaming().DisablePing().MaximumRetries(0)
            .OnRequestCompleted(call =>
            {
                if (call.RequestBodyInBytes != null)
                    bodies.Add(Encoding.UTF8.GetString(call.RequestBodyInBytes));
            });
        return new ElasticSearchServiceImpl(new ElasticClient(settings),
            Options.Create(new ElasticSearchOptions()), NullLogger<ElasticSearchServiceImpl>.Instance);
    }

    private static string Page(int start, int count) => JsonSerializer.Serialize(new
    {
        took = 1, timed_out = false,
        _shards = new { total = 1, successful = 1, skipped = 0, failed = 0 },
        hits = new
        {
            total = new { value = 201, relation = "eq" },
            hits = Enumerable.Range(start, count).Select(i => new
            {
                _index = "flowable-audit-records", _id = $"audit-{i}",
                _source = new { id = $"audit-{i}", processInstanceId = "instance-1", operatedAt = "2026-10-14T01:00:00Z" },
                sort = new object[] { 1, $"audit-{i}" }
            })
        }
    });

    private sealed class ResponsesConnection : InMemoryConnection
    {
        private readonly Queue<(string Body, int Status)> _responses;
        public ResponsesConnection(IEnumerable<(string Body, int Status)> responses) => _responses = new(responses);
        public override Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken cancellationToken)
        {
            if (data.Uri.AbsolutePath == "/")
                return base.RequestAsync<TResponse>(data, cancellationToken);
            var response = _responses.Dequeue();
            return ReturnConnectionStatusAsync<TResponse>(data, cancellationToken,
                Encoding.UTF8.GetBytes(response.Body), response.Status, "application/json");
        }
    }
}
