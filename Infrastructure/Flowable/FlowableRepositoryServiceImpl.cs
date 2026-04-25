using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlowableWrapper.Domain.Flowable;
using Microsoft.Extensions.Logging;

namespace FlowableWrapper.Infrastructure.Flowable
{
    public class FlowableRepositoryServiceImpl : IFlowableRepositoryService
    {
        private readonly FlowableHttpClient _http;
        private readonly ILogger<FlowableRepositoryServiceImpl> _logger;

        public FlowableRepositoryServiceImpl(
            FlowableHttpClient http,
            ILogger<FlowableRepositoryServiceImpl> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<FlowableDeployment> DeployBpmnAsync(
            string fileName,
            string bpmnXml)
        {
            // Flowable 部署接口需要 multipart/form-data
            using var content = new MultipartFormDataContent();
            var fileContent = new StringContent(bpmnXml, Encoding.UTF8, "application/xml");
            content.Add(fileContent, "file", fileName);

            var result = await _http.PostMultipartAsync<FlowableDeploymentResponse>(
                "repository/deployments", content);

            _logger.LogInformation("BPMN 部署成功: {FileName} → DeploymentId={DeploymentId}",
                fileName, result.Id);

            return new FlowableDeployment
            {
                Id = result.Id,
                Name = result.Name,
                DeploymentTime = result.DeploymentTime
            };
        }

        public async Task<FlowableProcessDefinition> GetLatestProcessDefinitionByKeyAsync(
            string processDefinitionKey)
        {
            var result = await _http.GetAsync<FlowableProcessDefinitionListResponse>(
                $"repository/process-definitions?key={Uri.EscapeDataString(processDefinitionKey)}&latest=true&size=1");

            if (result?.Data == null || result.Data.Length == 0)
                return null;

            var def = result.Data[0];
            return new FlowableProcessDefinition
            {
                Id = def.Id,
                Key = def.Key,
                Name = def.Name,
                Version = def.Version,
                DeploymentId = def.DeploymentId,
                ResourceName = def.ResourceName
            };
        }

        public async Task DeleteDeploymentAsync(string deploymentId, bool cascade)
        {
            await _http.DeleteAsync(
                $"repository/deployments/{deploymentId}?cascade={cascade.ToString().ToLower()}");
        }

        public async Task<string> GetBpmnXmlByDefinitionIdAsync(string processDefinitionId)
        {
            try
            {
                return await _http.GetStringAsync(
                    $"repository/process-definitions/{processDefinitionId}/resourcedata");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "获取 BPMN XML 失败: {ProcessDefinitionId}", processDefinitionId);
                return null;
            }
        }

        // ── Flowable REST 响应 DTO ──────────────────────────────
        private class FlowableDeploymentResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("deploymentTime")] public DateTime DeploymentTime { get; set; }
        }

        private class FlowableProcessDefinitionListResponse
        {
            [JsonPropertyName("data")]
            public FlowableProcessDefinitionResponse[] Data { get; set; }
        }

        private class FlowableProcessDefinitionResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("key")] public string Key { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("version")] public int Version { get; set; }
            [JsonPropertyName("deploymentId")] public string DeploymentId { get; set; }
            [JsonPropertyName("resourceName")] public string ResourceName { get; set; }
        }
    }
}
