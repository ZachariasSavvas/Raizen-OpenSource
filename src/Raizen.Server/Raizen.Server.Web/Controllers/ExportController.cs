using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Web.Controllers;

[Route("export")]
[Authorize(Policy = "Operator")]
public class ExportController(
    IExportService exportService,
    IDiagnosticBundleService diagnosticBundles,
    IConfiguration config) : Controller
{
    /// <summary>
    /// Downloads an ISO 27001 compliance Excel report of all elevation requests
    /// within the selected period.
    /// </summary>
    /// <param name="period">3m = last 3 months, 6m = last 6 months, 1y = last year.</param>
    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromQuery] string period = "3m", CancellationToken ct = default)
    {
        var from = period switch
        {
            "6m" => DateTimeOffset.UtcNow.AddMonths(-6),
            "1y" => DateTimeOffset.UtcNow.AddYears(-1),
            _    => DateTimeOffset.UtcNow.AddMonths(-3),
        };

        var bytes    = await exportService.ExportRequestsAsync(from, ct);
        var filename = $"Raizen-Requests-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            filename);
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(
        [FromQuery] string? eventFilter,
        [FromQuery] string? userFilter,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var bytes    = await exportService.ExportAuditAsync(eventFilter, userFilter, from, to, ct);
        var filename = $"Raizen-AuditLog-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";

        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            filename);
    }

    [HttpGet("requests/{id:guid}/evidence")]
    public async Task<IActionResult> RequestEvidence(Guid id, CancellationToken ct = default)
    {
        var bundle = await exportService.ExportRequestEvidenceBundleAsync(id, ct);
        if (bundle is null) return NotFound();

        return File(bundle.Bytes, bundle.ContentType, bundle.FileName);
    }

    [HttpGet("endpoints/diagnostics")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> FleetEndpointDiagnostics(CancellationToken ct = default)
    {
        var bundle = await exportService.ExportEndpointDiagnosticsBundleAsync(
            null,
            config["AgentDeployment:CurrentVersion"],
            ct);
        if (bundle is null) return NotFound();

        return File(bundle.Bytes, bundle.ContentType, bundle.FileName);
    }

    [HttpGet("endpoints/{id:guid}/diagnostics")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> EndpointDiagnostics(Guid id, CancellationToken ct = default)
    {
        var bundle = await exportService.ExportEndpointDiagnosticsBundleAsync(
            id,
            config["AgentDeployment:CurrentVersion"],
            ct);
        if (bundle is null) return NotFound();

        return File(bundle.Bytes, bundle.ContentType, bundle.FileName);
    }

    [HttpGet("diagnostic-bundles/{id:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> DiagnosticBundle(Guid id, CancellationToken ct = default)
    {
        var bundle = await diagnosticBundles.DownloadAsync(id, ct);
        return bundle is null
            ? NotFound()
            : File(bundle.Value.Content, bundle.Value.ContentType, bundle.Value.FileName);
    }
}
