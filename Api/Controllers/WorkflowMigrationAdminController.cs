using FlowableWrapper.Application.Services;
using FlowableWrapper.Domain.Reliability;
using Microsoft.AspNetCore.Mvc;

namespace FlowableWrapper.Api.Controllers;

[ApiController]
[Route("api/admin/workflow-migrations")]
public sealed class WorkflowMigrationAdminController : ControllerBase
{
    private readonly WorkflowMigrationAppService _migration;
    private readonly IWorkflowReliabilityStore _store;

    public WorkflowMigrationAdminController(
        WorkflowMigrationAppService migration,
        IWorkflowReliabilityStore store)
    {
        _migration = migration;
        _store = store;
    }

    [HttpGet("bindings/{businessId}")]
    public async Task<IActionResult> GetBinding(
        string businessId,
        CancellationToken cancellationToken)
    {
        var binding = await _store.GetBusinessByBusinessIdAsync(
            businessId,
            cancellationToken);
        return binding == null
            ? NotFound()
            : Ok(new
            {
                binding.BusinessId,
                binding.BusinessType,
                binding.ProcessInstanceId,
                binding.ProcessDefinitionKey,
                binding.ProcessDefinitionVersion,
                binding.FlowState,
                binding.CallbackState,
                binding.DataVersion,
                binding.UpdatedAt
            });
    }

    /// <summary>
    /// Idempotently migrates one ES page into DM8. Dry-run first, then process
    /// from the last page to the first to avoid page drift from new writes.
    /// </summary>
    [HttpPost("es-bindings")]
    public async Task<ActionResult<WorkflowBindingMigrationResult>>
        MigrateEsBindings(
            [FromQuery] int pageIndex = 1,
            [FromQuery] int pageSize = 100,
            [FromQuery] bool dryRun = true,
            CancellationToken cancellationToken = default)
    {
        return Ok(await _migration.MigrateEsBindingsPageAsync(
            pageIndex,
            pageSize,
            dryRun,
            cancellationToken));
    }
}
