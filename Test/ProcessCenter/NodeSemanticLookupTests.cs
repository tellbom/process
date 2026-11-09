using System.Reflection;
using System.Text;
using Elasticsearch.Net;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Infrastructure.ElasticSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nest;
using Xunit;

namespace FlowableWrapper.Test.ProcessCenter;

public class NodeSemanticLookupTests
{
    [Fact]
    public async Task MissingLegacySemanticDocumentReturnsEmptyConfiguration()
    {
        var service = Create(404, "{\"_index\":\"semantics\",\"_id\":\"legacy\",\"found\":false}");
        Assert.Empty(await service.GetNodeSemanticMapAsync("legacy"));
    }

    [Theory]
    [InlineData(404, "{\"error\":{\"type\":\"index_not_found_exception\",\"reason\":\"no such index\"},\"status\":404}")]
    [InlineData(401, "{\"error\":{\"type\":\"security_exception\",\"reason\":\"authentication required\"},\"status\":401}")]
    [InlineData(503, "{\"error\":{\"type\":\"unavailable_shards_exception\",\"reason\":\"unavailable\"},\"status\":503}")]
    [InlineData(404, "{}")]
    public async Task QueryFailuresAreNotTreatedAsMissingConfiguration(int status, string body)
    {
        var service = Create(status, body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetNodeSemanticMapAsync("legacy"));
    }

    [Fact]
    public async Task MalformedResponsesAreNotTreatedAsMissingConfiguration()
    {
        var service = Create(404, "not-json");
        await Assert.ThrowsAsync<UnexpectedElasticsearchClientException>(() => service.GetNodeSemanticMapAsync("legacy"));
    }

    private static ElasticSearchServiceImpl Create(int status, string body)
    {
        var service = new ElasticSearchServiceImpl(Options.Create(new ElasticSearchOptions { Uri = "http://elasticsearch.test" }), NullLogger<ElasticSearchServiceImpl>.Instance);
        var connection = new InMemoryConnection(Encoding.UTF8.GetBytes(body), status);
        var client = new ElasticClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("http://elasticsearch.test")), connection));
        typeof(ElasticSearchServiceImpl).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, client);
        return service;
    }
}
