namespace FlowableWrapper.Infrastructure.ElasticSearch;

public enum ElasticsearchIndexDecision
{
    Exists,
    Create,
    Fail
}

public static class ElasticsearchIndexProbe
{
    public static ElasticsearchIndexDecision Decide(
        bool isValid,
        bool exists,
        int? httpStatusCode)
    {
        if (exists)
            return ElasticsearchIndexDecision.Exists;

        if (httpStatusCode == 404)
            return ElasticsearchIndexDecision.Create;

        return isValid
            ? ElasticsearchIndexDecision.Create
            : ElasticsearchIndexDecision.Fail;
    }
}
