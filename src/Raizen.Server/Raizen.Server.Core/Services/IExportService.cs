namespace Raizen.Server.Core.Services;

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
}
