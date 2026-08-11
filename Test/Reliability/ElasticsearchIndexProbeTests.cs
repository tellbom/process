using FlowableWrapper.Infrastructure.ElasticSearch;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

public class ElasticsearchIndexProbeTests
{
    [Fact]
    public void Missing_index_404_is_a_create_decision_not_a_probe_failure()
    {
        var decision = ElasticsearchIndexProbe.Decide(
            isValid: false,
            exists: false,
            httpStatusCode: 404);

        Assert.Equal(ElasticsearchIndexDecision.Create, decision);
    }

    [Fact]
    public void Transport_failure_remains_a_probe_failure()
    {
        var decision = ElasticsearchIndexProbe.Decide(
            isValid: false,
            exists: false,
            httpStatusCode: 503);

        Assert.Equal(ElasticsearchIndexDecision.Fail, decision);
    }
}
