using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.Enums;
using System.Text.Json;

namespace Raizen.Server.Core.Services;

public sealed class ExportService(IDbContextFactory<RaizenDbContext> dbFactory) : IExportService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ISO 27001 column headers
    private static readonly string[] Headers =
    [
        "Request ID",
        "Status",
        "Action Name",
        "Action Type",
        "Target Machine",
        "Machine ID",
        "Requester UPN",
        "Requester Name",
        "Ticket Reference",
        "Justification",
        "Submitted At (UTC)",
        "Approval Expires (UTC)",
        "Reviewer UPN",
        "Review Decision",
        "Reviewer Note",
        "Reviewed At (UTC)",
        "Execution Status",
        "Executed At (UTC)",
        "Execution Result",
        "Execution Error",
        "Parameters",
    ];

    public async Task<byte[]> ExportRequestsAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var requests = await db.ElevationRequests
            .Include(r => r.ActionDefinition)
            .Include(r => r.Endpoint)
            .Where(r => r.SubmittedAt >= from)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync(ct);

        using var wb = new XLWorkbook();

        BuildRequestsSheet(wb, requests, from);
        BuildInfoSheet(wb, requests.Count, from);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildRequestsSheet(XLWorkbook wb, List<ElevationRequest> requests, DateTimeOffset from)
    {
        var ws = wb.Worksheets.Add("Elevation Requests");

        // ── Header row ──────────────────────────────────────────────────────────
        var navy    = XLColor.FromHtml("#1A2B4B");
        var white   = XLColor.White;
        var headerRow = ws.Row(1);
        headerRow.Style.Font.Bold      = true;
        headerRow.Style.Font.FontColor = XLColor.White;
        headerRow.Style.Fill.BackgroundColor = navy;
        headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        for (int c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];

        ws.SheetView.FreezeRows(1);

        // ── Data rows ────────────────────────────────────────────────────────────
        string dtFormat = "yyyy-MM-dd HH:mm:ss";

        for (int i = 0; i < requests.Count; i++)
        {
            var r   = requests[i];
            int row = i + 2;

            ws.Cell(row, 1).Value  = r.Id.ToString();
            ws.Cell(row, 2).Value  = r.Status.ToString();
            ws.Cell(row, 3).Value  = r.ActionDefinition?.DisplayName ?? string.Empty;
            ws.Cell(row, 4).Value  = r.ActionDefinition?.ActionType.ToString() ?? string.Empty;
            ws.Cell(row, 5).Value  = r.Endpoint?.MachineName ?? string.Empty;
            ws.Cell(row, 6).Value  = r.Endpoint?.MachineId  ?? string.Empty;
            ws.Cell(row, 7).Value  = r.RequesterUpn;
            ws.Cell(row, 8).Value  = r.RequesterDisplayName;
            ws.Cell(row, 9).Value  = r.TicketReference ?? string.Empty;
            ws.Cell(row, 10).Value = r.Justification;

            // DateTime columns — stored as plain text in ISO-8601 UTC to avoid locale ambiguity
            ws.Cell(row, 11).Value = r.SubmittedAt.UtcDateTime.ToString(dtFormat);
            ws.Cell(row, 12).Value = r.ExpiresAt.UtcDateTime.ToString(dtFormat);

            ws.Cell(row, 13).Value = r.ReviewerUpn ?? string.Empty;
            ws.Cell(row, 14).Value = ReviewDecision(r);
            ws.Cell(row, 15).Value = r.ReviewerNote ?? string.Empty;
            ws.Cell(row, 16).Value = r.ReviewedAt.HasValue
                ? r.ReviewedAt.Value.UtcDateTime.ToString(dtFormat)
                : string.Empty;

            ws.Cell(row, 17).Value = ExecutionStatus(r.Status);
            ws.Cell(row, 18).Value = r.ExecutedAt.HasValue
                ? r.ExecutedAt.Value.UtcDateTime.ToString(dtFormat)
                : string.Empty;
            ws.Cell(row, 19).Value = r.ExecutionResult ?? string.Empty;
            ws.Cell(row, 20).Value = r.ExecutionError  ?? string.Empty;
            ws.Cell(row, 21).Value = FormatParameters(r.ParametersJson);

            // ── Status-based row highlight on the Status cell (col 2) ──────────
            var statusCell = ws.Cell(row, 2);
            statusCell.Style.Fill.BackgroundColor = StatusColour(r.Status);
            statusCell.Style.Font.Bold = true;
        }

        // ── Column widths ────────────────────────────────────────────────────────
        ws.Columns().AdjustToContents();
        foreach (var col in ws.Columns())
        {
            if (col.Width > 60) col.Width = 60;
            if (col.Width < 12) col.Width = 12;
        }

        // ── Auto-filter on header row ─────────────────────────────────────────
        if (requests.Count > 0)
            ws.RangeUsed()?.SetAutoFilter();
    }

    private static void BuildInfoSheet(XLWorkbook wb, int rowCount, DateTimeOffset from)
    {
        var ws = wb.Worksheets.Add("Export Info");

        var navy = XLColor.FromHtml("#1A2B4B");

        void AddRow(int row, string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = navy;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(row, 2).Value = value;
        }

        AddRow(1, "Export Generated (UTC)", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(2, "Period From (UTC)",       from.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(3, "Period To (UTC)",          DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(4, "Total Requests Exported",  rowCount.ToString());
        AddRow(5, "Standard",                 "ISO/IEC 27001:2022 — A.8.15 Logging, A.5.18 Access Rights");
        AddRow(6, "System",                   "Raizen Brokered JIT Elevation Platform");

        ws.Column(1).Width = 32;
        ws.Column(2).AdjustToContents();
        if (ws.Column(2).Width < 30) ws.Column(2).Width = 30;
    }

    // ── Audit log export ────────────────────────────────────────────────────────

    private static readonly string[] AuditHeaders =
    [
        "Time (UTC)",
        "Event",
        "Actor UPN",
        "Requester UPN",
        "Approved By",
        "Machine",
        "IP Address",
        "Detail",
        "Request ID",
    ];

    public async Task<byte[]> ExportAuditAsync(
        string? eventFilter,
        string? userFilter,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(eventFilter))
            q = q.Where(x => x.Event.Contains(eventFilter));
        if (!string.IsNullOrEmpty(userFilter))
            q = q.Where(x => x.ActorUpn.Contains(userFilter) ||
                              (x.Request != null && x.Request.RequesterUpn.Contains(userFilter)));
        if (from.HasValue)
            q = q.Where(x => x.OccurredAt >= new DateTimeOffset(from.Value, TimeSpan.Zero));
        if (to.HasValue)
            q = q.Where(x => x.OccurredAt <= new DateTimeOffset(to.Value.AddDays(1), TimeSpan.Zero));

        var items = await q
            .OrderBy(x => x.OccurredAt)
            .Select(x => new
            {
                x.OccurredAt,
                x.Event,
                x.ActorUpn,
                RequesterUpn  = x.Request != null ? x.Request.RequesterUpn : null,
                ApproverUpn   = x.Request != null ? x.Request.ReviewerUpn  : null,
                TargetMachine = x.Request != null ? x.Request.Endpoint.MachineName : x.TargetMachine,
                x.IpAddress,
                x.Detail,
                x.RequestId,
            })
            .ToListAsync(ct);

        using var wb = new XLWorkbook();

        // ── Audit sheet ─────────────────────────────────────────────────────────
        var ws   = wb.Worksheets.Add("Audit Log");
        var navy = XLColor.FromHtml("#1A2B4B");

        var headerRow = ws.Row(1);
        headerRow.Style.Font.Bold             = true;
        headerRow.Style.Font.FontColor        = XLColor.White;
        headerRow.Style.Fill.BackgroundColor  = navy;
        headerRow.Style.Alignment.Horizontal  = XLAlignmentHorizontalValues.Left;

        for (int c = 0; c < AuditHeaders.Length; c++)
            ws.Cell(1, c + 1).Value = AuditHeaders[c];

        ws.SheetView.FreezeRows(1);

        const string dtFormat = "yyyy-MM-dd HH:mm:ss";
        var altRow = XLColor.FromHtml("#F8F9FA");

        for (int i = 0; i < items.Count; i++)
        {
            var e   = items[i];
            int row = i + 2;

            ws.Cell(row, 1).Value = e.OccurredAt.UtcDateTime.ToString(dtFormat);
            ws.Cell(row, 2).Value = e.Event;
            ws.Cell(row, 3).Value = e.ActorUpn;
            ws.Cell(row, 4).Value = e.RequesterUpn  ?? string.Empty;
            ws.Cell(row, 5).Value = e.ApproverUpn   ?? string.Empty;
            ws.Cell(row, 6).Value = e.TargetMachine ?? string.Empty;
            ws.Cell(row, 7).Value = e.IpAddress     ?? string.Empty;
            ws.Cell(row, 8).Value = e.Detail        ?? string.Empty;
            ws.Cell(row, 9).Value = e.RequestId?.ToString() ?? string.Empty;

            if (i % 2 == 1)
                ws.Row(row).Style.Fill.BackgroundColor = altRow;
        }

        ws.Columns().AdjustToContents();
        foreach (var col in ws.Columns())
        {
            if (col.Width > 60) col.Width = 60;
            if (col.Width < 12) col.Width = 12;
        }

        if (items.Count > 0)
            ws.RangeUsed()?.SetAutoFilter();

        // ── Info sheet ──────────────────────────────────────────────────────────
        var info = wb.Worksheets.Add("Export Info");

        void AddInfoRow(int row, string label, string value)
        {
            info.Cell(row, 1).Value = label;
            info.Cell(row, 1).Style.Font.Bold            = true;
            info.Cell(row, 1).Style.Fill.BackgroundColor = navy;
            info.Cell(row, 1).Style.Font.FontColor       = XLColor.White;
            info.Cell(row, 2).Value = value;
        }

        AddInfoRow(1, "Export Generated (UTC)", DateTime.UtcNow.ToString(dtFormat));
        AddInfoRow(2, "Filter: Event",          eventFilter ?? "(all)");
        AddInfoRow(3, "Filter: User",           userFilter  ?? "(all)");
        AddInfoRow(4, "Filter: From (UTC)",     from?.ToString("yyyy-MM-dd") ?? "(all)");
        AddInfoRow(5, "Filter: To (UTC)",       to?.ToString("yyyy-MM-dd")   ?? "(all)");
        AddInfoRow(6, "Total Entries Exported", items.Count.ToString());
        AddInfoRow(7, "System",                 "Raizen Brokered JIT Elevation Platform");

        info.Column(1).Width = 32;
        info.Column(2).AdjustToContents();
        if (info.Column(2).Width < 30) info.Column(2).Width = 30;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ReviewDecision(ElevationRequest r) => r.Status switch
    {
        RequestStatus.Denied  => "Denied",
        RequestStatus.Pending => "—",
        RequestStatus.Expired => "—",
        RequestStatus.Cancelled => "—",
        _ when r.ReviewerUpn == "system:auto-approve" => "Auto-Approved",
        _ when r.ReviewedAt.HasValue => "Approved",
        _ => "—",
    };

    private static string ExecutionStatus(RequestStatus status) => status switch
    {
        RequestStatus.Succeeded => "Succeeded",
        RequestStatus.Failed    => "Failed",
        RequestStatus.Executing => "Executing",
        _                       => "—",
    };

    private static XLColor StatusColour(RequestStatus status) => status switch
    {
        RequestStatus.Succeeded => XLColor.FromHtml("#D4EDDA"),   // green
        RequestStatus.Approved  => XLColor.FromHtml("#CCE5FF"),   // blue
        RequestStatus.Pending   => XLColor.FromHtml("#FFF3CD"),   // amber
        RequestStatus.Denied    => XLColor.FromHtml("#F8D7DA"),   // red
        RequestStatus.Failed    => XLColor.FromHtml("#F8D7DA"),   // red
        RequestStatus.Expired   => XLColor.FromHtml("#E2E3E5"),   // grey
        RequestStatus.Cancelled => XLColor.FromHtml("#E2E3E5"),   // grey
        RequestStatus.Executing => XLColor.FromHtml("#D1ECF1"),   // teal
        _                       => XLColor.NoColor,
    };

    private static string FormatParameters(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson) || parametersJson == "{}")
            return string.Empty;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson, JsonOpts);
            if (dict is null) return string.Empty;

            var pairs = dict
                .Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
                .Select(kv => $"{kv.Key}: {kv.Value}");

            return string.Join(" | ", pairs);
        }
        catch
        {
            return parametersJson; // fallback: raw JSON
        }
    }
}
