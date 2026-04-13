using System.Collections.Generic;
using System.Threading.Tasks;
using FlowableWrapper.Application.Dtos;
using FlowableWrapper.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.HttpApi.Controllers
{
    [Route("api/flowable/bpmn")]
    public class BpmnController : ControllerBase
    {
        private readonly BpmnDeploymentAppService _bpmnService;

        public BpmnController(BpmnDeploymentAppService bpmnService)
        {
            _bpmnService = bpmnService;
        }

        /// <summary>
        /// 部署 BPMN 文件
        /// POST /api/flowable/bpmn/deploy
        ///
        /// Content-Type: multipart/form-data
        ///   file          : .bpmn 文件
        ///   slotConfigJson: 节点配置 JSON 字符串（见 slotConfig_v2.json）
        /// </summary>
        [HttpPost("deploy")]
        [Consumes("multipart/form-data")]
        public async Task<BpmnDeploymentResponse> Deploy(
            [FromForm] IFormFile file,
            [FromForm] string slotConfigJson)
        {
            return await _bpmnService.DeployAsync(file, slotConfigJson);
        }

        /// <summary>
        /// 查询流程定义节点语义（含驳回选项和 Slot 定义）
        /// GET /api/flowable/bpmn/{processDefinitionKey}/nodes
        /// </summary>
        [HttpGet("{processDefinitionKey}/nodes")]
        public async Task<List<ProcessDefinitionNodeDto>> GetNodes(
            string processDefinitionKey)
        {
            return await _bpmnService.GetProcessDefinitionNodesAsync(processDefinitionKey);
        }

        /// <summary>
        /// 删除部署
        /// DELETE /api/flowable/bpmn/deployments/{deploymentId}?cascade=true
        /// </summary>
        [HttpDelete("deployments/{deploymentId}")]
        public async Task<IActionResult> DeleteDeployment(
            string deploymentId,
            [FromQuery] bool cascade = true)
        {
            await _bpmnService.DeleteDeploymentAsync(deploymentId, cascade);
            return Ok(new { message = "部署删除成功" });
        }
    }
}