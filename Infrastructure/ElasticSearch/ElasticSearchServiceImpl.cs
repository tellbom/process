using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nest;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.ElasticSearch
{
    /// <summary>
    /// ElasticSearch 服务实现 — NEST 7.x
    ///
    /// 修复：ES 自动创建 mapping 时字符串字段类型为 text+keyword 子字段
    /// term 查询必须使用 "fieldName.keyword"，否则分词后匹配失败
    /// </summary>
    public class ElasticSearchServiceImpl : IElasticSearchService
    {
        private readonly IElasticClient _client;
        private readonly ElasticSearchOptions _options;
        private readonly ILogger<ElasticSearchServiceImpl> _logger;

        public ElasticSearchServiceImpl(
            IOptions<ElasticSearchOptions> options,
            ILogger<ElasticSearchServiceImpl> logger)
        {
            _options = options.Value;
            _logger = logger;

            var uri = new Uri(_options.Uri);
            var settings = new ConnectionSettings(uri)
                .DefaultIndex(_options.IndexName)
                .DefaultMappingFor<ProcessMetadataDocument>(
                    m => m.IdProperty(p => p.ProcessInstanceId));

            if (!string.IsNullOrWhiteSpace(_options.Username)
                && !string.IsNullOrWhiteSpace(_options.Password))
                settings.BasicAuthentication(_options.Username, _options.Password);

            _client = new ElasticClient(settings);
        }

        // ═══════════════════════════════════════════════════════════
        // 流程元数据
        // ═══════════════════════════════════════════════════════════

        public async Task IndexProcessMetadataAsync(ProcessMetadataDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(document.ProcessInstanceId))
                throw new ArgumentException("ProcessInstanceId 不能为空");

            var response = await _client.IndexAsync(document, idx => idx
                .Index(_options.IndexName)
                .Id(document.ProcessInstanceId)
                .Refresh(Elasticsearch.Net.Refresh.WaitFor));

            if (!response.IsValid)
            {
                _logger.LogError("索引流程元数据失败: {Error}", response.DebugInformation);
                throw new Exception($"索引流程元数据失败: {response.DebugInformation}");
            }

            _logger.LogInformation(
                "流程元数据写入成功: ProcessInstanceId={ProcessInstanceId}",
                document.ProcessInstanceId);
        }

        public async Task<ProcessMetadataDocument> GetProcessMetadataAsync(
            string processInstanceId)
        {
            var response = await _client.GetAsync<ProcessMetadataDocument>(
                processInstanceId, g => g.Index(_options.IndexName));

            if (!response.IsValid || !response.Found) return null;
            return response.Source;
        }

        public async Task<Dictionary<string, ProcessMetadataDocument>> GetProcessMetadataBatchAsync(
    List<string> processInstanceIds)
        {
            if (processInstanceIds == null || !processInstanceIds.Any())
                return new Dictionary<string, ProcessMetadataDocument>();

            var response = await _client.MultiGetAsync(m => m
                .Index(_options.IndexName)
                .GetMany<ProcessMetadataDocument>(processInstanceIds));

            var result = new Dictionary<string, ProcessMetadataDocument>();
            if (!response.IsValid) return result;

            foreach (var hit in response.Hits)
            {
                if (hit.Found && hit.Source is ProcessMetadataDocument doc)
                {
                    result[hit.Id] = doc;
                }
            }

            return result;
        }

        /// <summary>
        /// 按业务 ID 查询运行中的流程
        /// 修复：使用 .keyword 子字段，避免 text 字段 term 查询因分词失败
        /// </summary>
        public async Task<ProcessMetadataDocument> GetProcessMetadataByBusinessIdAsync(
            string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId))
                throw new ArgumentException("BusinessId 不能为空");

            var response = await _client.SearchAsync<ProcessMetadataDocument>(s => s
                .Index(_options.IndexName)
                .Query(q => q
                    .Bool(b => b
                        .Must(
                            m => m.Term(t => t.Field("businessId.keyword").Value(businessId)),
                            m => m.Term(t => t.Field("status.keyword").Value("running"))
                        )
                    )
                )
                .Size(1));

            if (!response.IsValid || !response.Documents.Any())
            {
                _logger.LogWarning(
                    "未找到运行中的流程: BusinessId={BusinessId}", businessId);
                return null;
            }

            return response.Documents.First();
        }

        public async Task UpdateProcessStatusAsync(
            string processInstanceId,
            string status,
            DateTime? completedTime = null)
        {
            if (string.IsNullOrWhiteSpace(processInstanceId))
                throw new ArgumentException("ProcessInstanceId 不能为空");
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException("Status 不能为空");

            var scriptParams = new Dictionary<string, object> { ["status"] = status };
            if (completedTime.HasValue)
                scriptParams["completedTime"] = completedTime.Value;

            var response = await _client.UpdateAsync<ProcessMetadataDocument>(
                processInstanceId, u => u
                    .Index(_options.IndexName)
                    .Script(s => s
                        .Source("ctx._source.status = params.status; " +
                                "if (params.containsKey('completedTime')) " +
                                "{ ctx._source.completedTime = params.completedTime; }")
                        .Params(scriptParams)));

            if (!response.IsValid)
            {
                _logger.LogError(
                    "更新流程状态失败: {ProcessInstanceId}, {Error}",
                    processInstanceId, response.DebugInformation);
                throw new Exception($"更新流程状态失败: {response.DebugInformation}");
            }

            _logger.LogInformation(
                "流程状态更新成功: ProcessInstanceId={ProcessInstanceId}, Status={Status}",
                processInstanceId, status);
        }

        public async Task DeleteProcessMetadataAsync(string processInstanceId)
        {
            await _client.DeleteAsync<ProcessMetadataDocument>(
                processInstanceId, d => d.Index(_options.IndexName));
        }

        // ═══════════════════════════════════════════════════════════
        // 节点语义（流程定义级别）
        // ═══════════════════════════════════════════════════════════

        public async Task SaveNodeSemanticMapAsync(
            string processDefinitionKey,
            Dictionary<string, NodeSemanticInfo> nodeSemanticMap)
        {
            var doc = new ProcessDefinitionSemanticDocument
            {
                Id = processDefinitionKey,
                ProcessDefinitionKey = processDefinitionKey,
                NodeSemanticMap = nodeSemanticMap,
                LastUpdatedTime = DateTime.UtcNow
            };

            var response = await _client.IndexAsync(doc, idx => idx
                .Index(_options.SemanticIndexName)
                .Id(processDefinitionKey)
                .Refresh(Elasticsearch.Net.Refresh.WaitFor));

            if (!response.IsValid)
            {
                _logger.LogError(
                    "写入节点语义失败: {Key}, {Error}",
                    processDefinitionKey, response.DebugInformation);
                throw new Exception($"写入节点语义失败: {response.DebugInformation}");
            }

            _logger.LogInformation(
                "节点语义写入成功: {Key}, 节点数={Count}",
                processDefinitionKey, nodeSemanticMap.Count);
        }

        public async Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(
            string processDefinitionKey)
        {
            var response = await _client.GetAsync<ProcessDefinitionSemanticDocument>(
                processDefinitionKey,
                g => g.Index(_options.SemanticIndexName));

            if (!response.IsValid || !response.Found)
            {
                _logger.LogWarning("未找到节点语义配置: {Key}", processDefinitionKey);
                return new Dictionary<string, NodeSemanticInfo>();
            }

            return response.Source.NodeSemanticMap
                   ?? new Dictionary<string, NodeSemanticInfo>();
        }

        // ═══════════════════════════════════════════════════════════
        // 审计记录
        // ═══════════════════════════════════════════════════════════

        public async Task IndexAuditRecordAsync(ProcessAuditRecord record)
        {
            var response = await _client.IndexAsync(record, idx => idx
                .Index(_options.AuditIndexName)
                .Id(record.Id)
                .Refresh(Elasticsearch.Net.Refresh.WaitFor));

            if (!response.IsValid)
            {
                _logger.LogError(
                    "写入审计记录失败: {TaskId}, {Error}",
                    record.TaskId, response.DebugInformation);
                throw new Exception($"写入审计记录失败: {response.DebugInformation}");
            }
        }

        public async Task<List<ProcessAuditRecord>> QueryAuditRecordsByBusinessIdAsync(
            string businessId)
        {
            var response = await _client.SearchAsync<ProcessAuditRecord>(s => s
                .Index(_options.AuditIndexName)
                .Query(q => q
                    .Term(t => t.Field("businessId.keyword").Value(businessId)))
                .Sort(so => so.Ascending(f => f.OperatedAt))
                .Size(200));

            if (!response.IsValid) return new List<ProcessAuditRecord>();
            return response.Documents.ToList();
        }

        // ═══════════════════════════════════════════════════════════
        // 流程列表查询
        // ═══════════════════════════════════════════════════════════

        public async Task<(List<ProcessMetadataDocument> Items, int Total)>
            QueryProcessListAsync(ProcessListRequest request)
        {
            var from = (request.PageIndex - 1) * Math.Min(request.PageSize, 100);
            var size = Math.Min(request.PageSize, 100);

            var response = await _client.SearchAsync<ProcessMetadataDocument>(s => s
                .Index(_options.IndexName)
                .From(from)
                .Size(size)
                .Sort(so => so.Descending(f => f.CreatedTime))
                .Query(q => q
                    .Bool(b =>
                    {
                        var must = new List<Func<
                            QueryContainerDescriptor<ProcessMetadataDocument>,
                            QueryContainer>>();
                        if (!string.IsNullOrWhiteSpace(request.BusinessId))
                            must.Add(m => m.Term(t =>
                                t.Field("businessId.keyword").Value(request.BusinessId)));
                        if (!string.IsNullOrWhiteSpace(request.BusinessType))
                            must.Add(m => m.Term(t =>
                                t.Field("businessType.keyword").Value(request.BusinessType)));
                        if (!string.IsNullOrWhiteSpace(request.Status))
                            must.Add(m => m.Term(t =>
                                t.Field("status.keyword").Value(request.Status)));
                        if (!string.IsNullOrWhiteSpace(request.CreatedBy))
                            must.Add(m => m.Term(t =>
                                t.Field("createdBy.keyword").Value(request.CreatedBy)));
                        if (request.CreatedTimeFrom.HasValue || request.CreatedTimeTo.HasValue)
                            must.Add(m => m.DateRange(dr =>
                            {
                                dr.Field(f => f.CreatedTime);
                                if (request.CreatedTimeFrom.HasValue)
                                    dr.GreaterThanOrEquals(request.CreatedTimeFrom.Value);
                                if (request.CreatedTimeTo.HasValue)
                                    dr.LessThanOrEquals(request.CreatedTimeTo.Value);
                                return dr;
                            }));

                        if (must.Any()) b.Must(must.ToArray());
                        return b;
                    })
                )
            );

            if (!response.IsValid)
            {
                _logger.LogError("查询流程列表失败: {Error}", response.DebugInformation);
                return (new List<ProcessMetadataDocument>(), 0);
            }

            return (response.Documents.ToList(), (int)response.Total);
        }
    }
}