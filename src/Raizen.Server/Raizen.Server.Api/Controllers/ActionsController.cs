using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Serilog;
using System.Security.Claims;

namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/actions")]
public sealed class ActionsController(IActionCatalogService catalog) : ControllerBase
{
    // ── Public: list enabled actions (used by tray app to populate form) ───────
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<List<ActionDefinitionDto>>> List(CancellationToken ct)
        => Ok(await catalog.ListAsync(includeDisabled: false, ct));

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<ActionDefinitionDto>> GetById(Guid id, CancellationToken ct)
    {
        var all = await catalog.ListAsync(includeDisabled: false, ct);
        var result = all.FirstOrDefault(x => x.Id == id);
        return result is null ? NotFound() : Ok(result);
    }

    // ── Admin: manage catalog ──────────────────────────────────────────────────
    [HttpGet("all")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<List<ActionDefinitionDto>>> ListAll(CancellationToken ct)
        => Ok(await catalog.ListAsync(includeDisabled: true, ct));

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ActionDefinitionDto>> Create(
        [FromBody] UpsertActionDefinitionDto dto,
        CancellationToken ct)
    {
        var actorUpn = GetActorUpn();
        try
        {
            var result = await catalog.CreateAsync(dto, actorUpn, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create action definition");
            return BadRequest(new { error = "The request could not be processed." });
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<ActionDefinitionDto>> Update(
        Guid id,
        [FromBody] UpsertActionDefinitionDto dto,
        CancellationToken ct)
    {
        try
        {
            var result = await catalog.UpdateAsync(id, dto, GetActorUpn(), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/enable")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        await catalog.SetEnabledAsync(id, true, GetActorUpn(), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/disable")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        await catalog.SetEnabledAsync(id, false, GetActorUpn(), ct);
        return NoContent();
    }

    private string GetActorUpn() =>
        User.FindFirstValue(ClaimTypes.Upn)
     ?? User.FindFirstValue("preferred_username")
     ?? User.FindFirstValue(ClaimTypes.Email)
     ?? "unknown";

}
