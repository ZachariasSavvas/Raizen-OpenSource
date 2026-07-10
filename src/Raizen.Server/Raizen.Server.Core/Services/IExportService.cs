namespace Raizen.Server.Core.Services;

public sealed record ExportBundle(string FileName, string ContentType, byte[] Bytes);

public interface IExportService
{
    /// <summary>
    /// Returns an .xlsx workbook (as bytes) containing all elevation requests submitted
    /// on or after <paramref name="from"/>.  Suitable for ISO 27001 access-control audits.
    /// </summary>
    Task<byte[]> ExportRequestsAsync(DateTimeOffset from, CancellationToken ct = default);

    /// <summary>
    /// Returns an .xlsx workbook (as bytes) of audit log entries matching the given filters.
    /// </summary>
    Task<byte[]> ExportAuditAsync(
        string? eventFilter,
        string? userFilter,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a ZIP evidence bundle for a single request, including request data,
    /// approvals, comments, related audit events, and a human-readable workbook.
    /// </summary>
    Task<ExportBundle?> ExportRequestEvidenceBundleAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Returns a ZIP diagnostics bundle for one endpoint or the fleet health view.
    /// Secrets and API key hashes are intentionally excluded.
    /// </summary>
    Task<ExportBundle?> ExportEndpointDiagnosticsBundleAsync(
        Guid? endpointId,
        string? latestAgentVersion,
        CancellationToken ct = default);
}
