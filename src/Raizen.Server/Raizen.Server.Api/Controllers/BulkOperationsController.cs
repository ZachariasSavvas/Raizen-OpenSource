using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/bulk-operations")]
[Authorize(Policy = "AdminOnly")]
public sealed class BulkOperationsController(IBulkOperationService bulkOps) : ControllerBase
{
    /// <summary>Submit a bulk operation targeting multiple endpoints.</summary>
    [HttpPost]
    public async Task<ActionResult<BulkOperationDto>> Submit(
        [FromBody] SubmitBulkOperationDto dto,
        CancellationToken ct)
    {
        var upn = User.Identity?.Name ?? "unknown";
        try
        {
            var result = await bulkOps.SubmitAsync(dto, upn, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get progress of a bulk operation including child request statuses.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BulkOperationDto>> GetById(Guid id, CancellationToken ct)
    {
        var result = await bulkOps.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>List all bulk operations (newest first).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<BulkOperationDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await bulkOps.ListAsync(page, pageSize, ct);
        return Ok(result);
    }
}
