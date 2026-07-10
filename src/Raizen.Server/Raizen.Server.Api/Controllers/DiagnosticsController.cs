using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/diagnostics")]
public sealed class DiagnosticsController(IDiagnosticBundleService diagnostics) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "EndpointOnly")]
    [RequestSizeLimit(14 * 1024 * 1024)]
    public async Task<ActionResult<DiagnosticBundleUploadResponseDto>> Upload(
        [FromBody] DiagnosticBundleUploadDto dto,
        CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("registration_id"), out var registrationId))
            return Unauthorized(new { error = "Missing registration_id claim" });
        try { return Ok(await diagnostics.UploadAsync(registrationId, dto, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
