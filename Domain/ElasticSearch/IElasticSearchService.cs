using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Domain.ElasticSearch;

namespace FlowableWrapper.Domain.Services
{
    public interface IElasticSearchService
    {
        // ── 流程元数据 ────────────────────────────────────────────
        Task IndexProcessMetadataAsync(ProcessMetadataDocument document);

        Task<ProcessMetadataDocument> GetProcessMetadataAsync(string processInstanceId);

        Task<Dictionary<string, ProcessMetadataDocument>> GetProcessMetadataBatchAsync(
            List<string> processInstanceIds);

        Task<ProcessMetadataDocument> GetProcessMetadataByBusinessIdAsync(string businessId);

        Task UpdateProcessStatusAsync(
            string processInstanceId,
            string status,
            DateTime? completedTime = null);

        Task DeleteProcessMetadataAsync(string processInstanceId);

        // ── 节点语义（流程定义级别，与实例无关）──────────────────
        /// <summary>
        /// 写入流程定义的节点语义映射（部署 BPMN 时调用）
        /// </summary>
        Task SaveNodeSemanticMapAsync(
            string processDefinitionKey,
            Dictionary<string, NodeSemanticInfo> nodeSemanticMap);

        /// <summary>
        /// 读取流程定义的节点语义映射
        /// </summary>
        Task<Dictionary<string, NodeSemanticInfo>> GetNodeSemanticMapAsync(
            string processDefinitionKey);

        // ── 审计记录 ──────────────────────────────────────────────
        /// <summary>
        /// 写入审批审计记录
        /// </summary>
        Task IndexAuditRecordAsync(ProcessAuditRecord record);

        /// <summary>
        /// 按业务 ID 查询审计记录（按时间正序）
        /// </summary>
        Task<List<ProcessAuditRecord>> QueryAuditRecordsByBusinessIdAsync(string businessId);

        // ── 流程列表查询 ──────────────────────────────────────────
        /// <summary>
        /// 分页查询流程列表
        /// </summary>
        Task<(List<ProcessMetadataDocument> Items, int Total)> QueryProcessListAsync(
            ProcessListRequest request);
    }
}