using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Api.Auth;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;
using System.Security.Claims;


namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/requests")]
public sealed class RequestsController(
    IRequestService requests,
    IPollResponseSigner pollSigner,
    IEndpointService endpoints) : ControllerBase
{
    private Guid GetRegistrationId() =>
        Guid.TryParse(User.FindFirstValue("registration_id"), out var id)
            ? id : throw new UnauthorizedAccessException("Missing registration_id claim");

    private string GetMachineId() =>
        User.FindFirstValue("machine_id")
            ?? throw new UnauthorizedAccessException("Missing machine_id claim");

    // ── Endpoint: submit a new request ─────────────────────────────────────────
    [HttpPost]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<ElevationRequestDto>> Submit(
        [FromBody] SubmitElevationRequestDto dto,
        CancellationToken ct)
    {
        var registrationId = GetRegistrationId();

        // Prefer the dedicated identity fields (new tray versions set these).
        // Fall back to legacy reserved parameter keys for backwards compatibility with older tray builds.
        var requesterUpn = !string.IsNullOrWhiteSpace(dto.RequesterUpn)
            ? dto.RequesterUpn
            : dto.Parameters.GetValueOrDefault("__requester_upn", "unknown");
        var requesterDisplayName = !string.IsNullOrWhiteSpace(dto.RequesterDisplayName)
            ? dto.RequesterDisplayName
            : dto.Parameters.GetValueOrDefault("__requester_display_name", string.Empty);

        // Always strip reserved keys so they are never stored as action parameters
        dto.Parameters.Remove("__requester_upn");
        dto.Parameters.Remove("__requester_display_name");

        try
        {
            var result = await requests.SubmitAsync(dto, requesterUpn, requesterDisplayName, registrationId, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    // ── Endpoint / Requester: poll status ─────────────────────────────────────
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<ActionResult<ElevationRequestDto>> GetById(Guid id, CancellationToken ct)
    {
        var result = await requests.GetByIdAsync(id, ct);
        if (result is null) return NotFound();

        // Endpoints may only read their own requests — return 404 to avoid confirming existence
        if (User.IsInRole(ApiKeyAuthHandler.RoleEndpoint))
        {
            var registrationId = GetRegistrationId();
            if (result.EndpointRegistrationId != registrationId) return NotFound();
        }

        return Ok(result);
    }

    // ── Requester: list own requests ───────────────────────────────────────────
    [HttpGet("mine")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<PagedResult<ElevationRequestDto>>> ListMine(
        [FromQuery] RequestStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var machineId = GetMachineId();
        var result = await requests.ListAsync(status, null, machineId, page, pageSize, ct);
        return Ok(result);
    }

    // ── Endpoint: cancel own pending request ───────────────────────────────────
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<ElevationRequestDto>> Cancel(Guid id, CancellationToken ct)
    {
        var machineId = GetMachineId();
        try
        {
            // Requester UPN is the machine identity in this context
            var result = await requests.CancelAsync(id, $"machine:{machineId}", ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Endpoint: get approved requests ready for execution ────────────────────
    [HttpGet("pending-execution")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<SignedPollResponseDto>> GetPendingExecution(CancellationToken ct)
    {
        var registrationId = GetRegistrationId();
        var items = await requests.GetPendingExecutionAsync(registrationId, ct);
        var signed = pollSigner.Sign(items);

        var ep = await endpoints.GetByIdAsync(registrationId, ct);
        if (ep?.UpdatePending == true)
        {
            signed.UpdateNow = true;
            await endpoints.AcknowledgeUpdateAsync(registrationId, ct);
        }

        return Ok(signed);
    }

    // ── Endpoint: atomically claim an approved request for execution ───────────
    [HttpPost("{id:guid}/executing")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<ElevationRequestDto>> MarkExecuting(Guid id, CancellationToken ct)
    {
        var registrationId = GetRegistrationId();
        var result = await requests.MarkAsExecutingAsync(id, registrationId, ct);
        if (result is null)
            return Conflict(new { error = "Request is not in Approved status, has expired, or does not belong to this endpoint." });
        return Ok(result);
    }

    // ── Endpoint: report execution result ─────────────────────────────────────
    [HttpPost("execution-result")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<ElevationRequestDto>> ReportResult(
        [FromBody] ExecutionResultDto dto,
        CancellationToken ct)
    {
        var registrationId = GetRegistrationId();
        try
        {
            var result = await requests.ReportExecutionResultAsync(dto, registrationId, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Admin: list all requests ───────────────────────────────────────────────
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<PagedResult<ElevationRequestDto>>> List(
        [FromQuery] RequestStatus? status,
        [FromQuery] string? requesterUpn,
        [FromQuery] string? machineId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var result = await requests.ListAsync(status, requesterUpn, machineId, page, pageSize, ct);
        return Ok(result);
    }

    // ── Admin / Approver: review a request ────────────────────────────────────
    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = "ApproverOrAdmin")]
    public async Task<ActionResult<ElevationRequestDto>> Review(
        Guid id,
        [FromBody] ReviewRequestDto dto,
        CancellationToken ct)
    {
        var reviewerUpn = User.FindFirstValue(ClaimTypes.Upn)
                       ?? User.FindFirstValue("preferred_username")
                       ?? User.FindFirstValue(ClaimTypes.Email)
                       ?? "unknown";
        try
        {
            var result = await requests.ReviewAsync(id, dto, reviewerUpn, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Admin: get individual approvals for a request ─────────────────────────
    [HttpGet("{id:guid}/approvals")]
    [Authorize(Policy = "ApproverOrAdmin")]
    public async Task<ActionResult<List<RequestApprovalDto>>> GetApprovals(Guid id, CancellationToken ct)
        => Ok(await requests.GetApprovalsAsync(id, ct));

    // ── Endpoint: get comments for a request ───────────────────────────────────
    [HttpGet("{id:guid}/comments")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<List<RequestCommentDto>>> GetComments(Guid id, CancellationToken ct)
    {
        var registrationId = GetRegistrationId();
        var req = await requests.GetByIdAsync(id, ct);
        if (req is null || req.EndpointRegistrationId != registrationId) return NotFound();
        return Ok(await requests.GetCommentsAsync(id, ct));
    }

    // ── Endpoint: add a comment ────────────────────────────────────────────────
    [HttpPost("{id:guid}/comments")]
    [Authorize(Policy = "EndpointOnly")]
    public async Task<ActionResult<RequestCommentDto>> AddComment(
        Guid id,
        [FromBody] AddCommentDto dto,
        CancellationToken ct)
    {
        var registrationId = GetRegistrationId();
        var machineId = GetMachineId();
        var req = await requests.GetByIdAsync(id, ct);
        if (req is null || req.EndpointRegistrationId != registrationId) return NotFound();

        try
        {
            var comment = await requests.AddCommentAsync(id, dto,
                authorUpn: $"machine:{machineId}",
                authorDisplayName: req.TargetMachine,
                isAdmin: false,
                ct: ct);
            return Ok(comment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
