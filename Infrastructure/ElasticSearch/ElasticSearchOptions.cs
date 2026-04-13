namespace FlowableWrapper.Infrastructure.ElasticSearch
{
    /// <summary>
    /// ES 连接配置，绑定到 appsettings.json 的 ElasticSearch 节点
    /// </summary>
    public class ElasticSearchOptions
    {
        public string Uri { get; set; }
        public string IndexName { get; set; } = "flowable-process-metadata";
        public string AuditIndexName { get; set; } = "flowable-audit-records";
        public string SemanticIndexName { get; set; } = "flowable-process-definition-semantic";
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
