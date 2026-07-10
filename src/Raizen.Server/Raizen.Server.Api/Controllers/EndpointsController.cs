using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Api.Auth;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using System.Security.Claims;
using System.Net.Mime;

namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/endpoints")]
public sealed class EndpointsController(
    IEndpointService endpoints,
    IRegistrationTokenService tokens,
    IMonitoringService monitoring) : ControllerBase
{
    /// <summary>
    /// Called once during endpoint agent installation.
    /// Requires a one-time registration token passed as a Bearer token by the installer.
    /// After registration the endpoint uses its machine-specific API key for all calls.
    /// </summary>
    [HttpPost("register")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<EndpointRegistrationDto>> Register(
        [FromHeader(Name = "X-Raizen-MachineId")] string machineId,
        [FromHeader(Name = "X-Raizen-NewApiKey")] string newApiKey,
        [FromBody] RegisterEndpointDto dto,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(newApiKey))
            return BadRequest(new { error = "X-Raizen-MachineId and X-Raizen-NewApiKey headers are required." });

        var result = await endpoints.RegisterAsync(machineId, newApiKey, dto, ct);
        return Ok(result);
    }

    /// <summary>Endpoint heartbeat — keeps LastSeenAt fresh and reports agent/OS version.</summary>
    [HttpPost("heartbeat")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<IActionResult> Heartbeat([FromBody] EndpointHeartbeatDto dto, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("registration_id"), out var registrationId))
            return Unauthorized(new { error = "Missing registration_id claim" });
        await endpoints.HeartbeatAsync(registrationId, dto, ct);
        await monitoring.EvaluateEndpointAsync(registrationId, ct);
        return NoContent();
    }

    // ── Admin operations ───────────────────────────────────────────────────────
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<List<EndpointRegistrationDto>>> List(CancellationToken ct)
        => Ok(await endpoints.ListAsync(ct));

    [HttpPost("{id:guid}/enable")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        var actorUpn = GetActorUpn();
        await endpoints.SetEnabledAsync(id, true, actorUpn, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/disable")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        var actorUpn = GetActorUpn();
        await endpoints.SetEnabledAsync(id, false, actorUpn, ct);
        return NoContent();
    }

    // ── Registration token management (admin) ─────────────────────────────────

    [HttpPost("registration-tokens")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<CreateRegistrationTokenResponseDto>> CreateToken(
        [FromBody] CreateRegistrationTokenDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Label))
            return BadRequest(new { error = "Label is required." });
        if (dto.MaxUses < 1)
            return BadRequest(new { error = "MaxUses must be at least 1." });
        if (dto.ExpiryHours.HasValue && dto.ExpiryHours.Value < 1)
            return BadRequest(new { error = "ExpiryHours must be at least 1." });

        var result = await tokens.CreateAsync(dto, GetActorUpn(), ct);
        return Ok(result);
    }

    [HttpGet("registration-tokens")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<List<RegistrationTokenDto>>> ListTokens(CancellationToken ct)
    {
        return Ok(await tokens.ListAsync(ct));
    }

    [HttpPost("registration-tokens/{id:guid}/revoke")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        await tokens.RevokeAsync(id, GetActorUpn(), ct);
        return NoContent();
    }

    // ── Manual agent update push (admin) ──────────────────────────────────────

    [HttpPost("push-update")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> PushUpdate([FromBody] PushUpdateDto dto, CancellationToken ct)
    {
        await endpoints.PushUpdateAsync(dto.RegistrationIds, GetActorUpn(), ct);
        return NoContent();
    }

    // ── Token exchange (no auth — endpoint calls this before it has an ApiKey) ─

    [HttpPost("exchange-token")]
    [AllowAnonymous]
    public async Task<ActionResult<ExchangeTokenResponseDto>> ExchangeToken(
        [FromHeader(Name = "X-Raizen-MachineId")] string machineId,
        [FromBody] ExchangeTokenDto dto,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId) || machineId.Length > 128)
            return BadRequest(new { error = "X-Raizen-MachineId header is required and must be 128 characters or fewer." });
        if (string.IsNullOrWhiteSpace(dto.RegistrationToken))
            return BadRequest(new { error = "RegistrationToken is required." });
        if (!string.IsNullOrEmpty(dto.MachineName) && dto.MachineName.Length > 253)
            return BadRequest(new { error = "MachineName must be 253 characters or fewer." });

        var result = await tokens.ExchangeAsync(dto, machineId, ct);
        if (result is null)
            return Unauthorized(new { error = "Invalid, expired, or exhausted registration token." });

        return Ok(result);
    }

    private string GetActorUpn() =>
        User.FindFirstValue(ClaimTypes.Upn)
     ?? User.FindFirstValue("preferred_username")
     ?? User.FindFirstValue(ClaimTypes.Email)
     ?? "unknown";
}
