using ClosedXML.Excel;
using System.IO.Compression;
using System.Text;
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

    public async Task<ExportBundle?> ExportRequestEvidenceBundleAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var request = await db.ElevationRequests
            .Include(r => r.ActionDefinition)
            .Include(r => r.Endpoint)
            .Include(r => r.Approvals)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null) return null;

        var comments = await db.RequestComments
            .Where(c => c.RequestId == requestId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var auditEntries = await db.AuditLogs
            .Where(a => a.RequestId == requestId)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync(ct);

        var exportedAt = DateTimeOffset.UtcNow;
        var evidence = new
        {
            ExportedAtUtc = exportedAt,
            BundleType = "request-evidence",
            Request = RequestEvidence(request),
            EndpointHealth = EndpointHealthEvidence(request.Endpoint),
            Approvals = request.Approvals
                .OrderBy(a => a.OccurredAt)
                .Select(a => new
                {
                    a.Id,
                    a.ApproverUpn,
                    Decision = a.Approved ? "Approved" : "Denied",
                    a.Note,
                    OccurredAtUtc = a.OccurredAt,
                })
                .ToList(),
            Comments = comments.Select(c => new
            {
                c.Id,
                c.AuthorUpn,
                c.AuthorDisplayName,
                Role = c.IsAdmin ? "Approver" : "Requester",
                c.Body,
                CreatedAtUtc = c.CreatedAt,
            }).ToList(),
            AuditEntries = auditEntries.Select(AuditEvidence).ToList(),
            AuditChain = new
            {
                IncludedAuditEntries = auditEntries.Count,
                AllIncludedRowsHaveHashes = auditEntries.All(a => !string.IsNullOrWhiteSpace(a.RowHash)),
                Note = "This bundle includes row hash fields for related audit entries. Run Audit Log > Verify Integrity for full-chain validation.",
            },
        };

        var workbook = BuildRequestEvidenceWorkbook(request, comments, auditEntries, exportedAt);
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddJson(zip, "manifest.json", new
            {
                ExportedAtUtc = exportedAt,
                BundleType = "request-evidence",
                RequestId = request.Id,
                Files = new[] { "request-evidence.json", "request-evidence.xlsx" },
                SecurityNote = "No endpoint API keys, API key hashes, private signing keys, or SMTP secrets are included.",
            });
            AddJson(zip, "request-evidence.json", evidence);
            AddBytes(zip, "request-evidence.xlsx", workbook);
        }

        var fileName = $"Raizen-RequestEvidence-{request.Id}-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip";
        return new ExportBundle(fileName, "application/zip", zipStream.ToArray());
    }

    public async Task<ExportBundle?> ExportEndpointDiagnosticsBundleAsync(
        Guid? endpointId,
        string? latestAgentVersion,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var endpointQuery = db.EndpointRegistrations.AsQueryable();
        if (endpointId.HasValue)
            endpointQuery = endpointQuery.Where(e => e.Id == endpointId.Value);

        var endpoints = await endpointQuery
            .OrderBy(e => e.MachineName)
            .ToListAsync(ct);

        if (endpointId.HasValue && endpoints.Count == 0) return null;

        var endpointIds = endpoints.Select(e => e.Id).ToList();
        var endpointNames = endpoints.Select(e => e.MachineName).ToList();
        var recentRequestLimit = endpointId.HasValue ? 100 : 500;

        var recentRequests = await db.ElevationRequests
            .Include(r => r.ActionDefinition)
            .Include(r => r.Endpoint)
            .Where(r => endpointIds.Contains(r.EndpointRegistrationId))
            .OrderByDescending(r => r.SubmittedAt)
            .Take(recentRequestLimit)
            .ToListAsync(ct);

        var requestIds = recentRequests.Select(r => r.Id).ToList();
        var recentAudit = await db.AuditLogs
            .Where(a => (a.RequestId.HasValue && requestIds.Contains(a.RequestId.Value))
                     || (a.TargetMachine != null && endpointNames.Contains(a.TargetMachine)))
            .OrderByDescending(a => a.OccurredAt)
            .Take(500)
            .ToListAsync(ct);

        var exportedAt = DateTimeOffset.UtcNow;
        var diagnostics = new
        {
            ExportedAtUtc = exportedAt,
            BundleType = endpointId.HasValue ? "endpoint-diagnostics" : "fleet-endpoint-diagnostics",
            LatestAgentVersion = latestAgentVersion,
            EndpointCount = endpoints.Count,
            Counts = new
            {
                Enabled = endpoints.Count(e => e.IsEnabled),
                Disabled = endpoints.Count(e => !e.IsEnabled),
                Online = endpoints.Count(IsOnline),
                HealthIssues = endpoints.Count(HasHealthIssue),
                PollSigningMissing = endpoints.Count(e => !e.PollSigningConfigured),
                UpdatePending = endpoints.Count(e => e.UpdatePending),
                Outdated = endpoints.Count(e => IsOutdated(e, latestAgentVersion)),
            },
            Endpoints = endpoints.Select(e => EndpointHealthEvidence(e, latestAgentVersion)).ToList(),
            RecentRequests = recentRequests.Select(RequestEvidence).ToList(),
            RecentAuditEntries = recentAudit.Select(AuditEvidence).ToList(),
            SecurityNote = "Endpoint API keys, API key hashes, previous key hashes, registration tokens, and private signing keys are not exported.",
        };

        var workbook = BuildEndpointDiagnosticsWorkbook(endpoints, recentRequests, recentAudit, latestAgentVersion, exportedAt);
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddJson(zip, "manifest.json", new
            {
                ExportedAtUtc = exportedAt,
                BundleType = endpointId.HasValue ? "endpoint-diagnostics" : "fleet-endpoint-diagnostics",
                EndpointId = endpointId,
                Files = new[] { "endpoint-diagnostics.json", "endpoint-diagnostics.xlsx" },
                SecurityNote = "No endpoint API keys, API key hashes, private signing keys, or SMTP secrets are included.",
            });
            AddJson(zip, "endpoint-diagnostics.json", diagnostics);
            AddBytes(zip, "endpoint-diagnostics.xlsx", workbook);
        }

        var namePart = endpointId.HasValue
            ? SanitiseFilePart(endpoints[0].MachineName)
            : "Fleet";
        var fileName = $"Raizen-EndpointDiagnostics-{namePart}-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip";
        return new ExportBundle(fileName, "application/zip", zipStream.ToArray());
    }

    private static byte[] BuildRequestEvidenceWorkbook(
        ElevationRequest request,
        List<RequestComment> comments,
        List<AuditLog> auditEntries,
        DateTimeOffset exportedAt)
    {
        using var wb = new XLWorkbook();
        var navy = XLColor.FromHtml("#1A2B4B");

        var summary = wb.Worksheets.Add("Request");
        AddWorkbookRow(summary, 1, "Request ID", request.Id.ToString(), navy);
        AddWorkbookRow(summary, 2, "Status", request.Status.ToString(), navy);
        AddWorkbookRow(summary, 3, "Action", request.ActionDefinition?.DisplayName ?? "", navy);
        AddWorkbookRow(summary, 4, "Action Type", request.ActionDefinition?.ActionType.ToString() ?? "", navy);
        AddWorkbookRow(summary, 5, "Target Machine", request.Endpoint?.MachineName ?? "", navy);
        AddWorkbookRow(summary, 6, "Machine ID", request.Endpoint?.MachineId ?? "", navy);
        AddWorkbookRow(summary, 7, "Requester", request.RequesterUpn, navy);
        AddWorkbookRow(summary, 8, "Requester Name", request.RequesterDisplayName, navy);
        AddWorkbookRow(summary, 9, "Ticket", request.TicketReference ?? "", navy);
        AddWorkbookRow(summary, 10, "Justification", request.Justification, navy);
        AddWorkbookRow(summary, 11, "Submitted UTC", FormatUtc(request.SubmittedAt), navy);
        AddWorkbookRow(summary, 12, "Expires UTC", FormatUtc(request.ExpiresAt), navy);
        AddWorkbookRow(summary, 13, "Reviewer", request.ReviewerUpn ?? "", navy);
        AddWorkbookRow(summary, 14, "Reviewer Note", request.ReviewerNote ?? "", navy);
        AddWorkbookRow(summary, 15, "Reviewed UTC", FormatUtc(request.ReviewedAt), navy);
        AddWorkbookRow(summary, 16, "Executed UTC", FormatUtc(request.ExecutedAt), navy);
        AddWorkbookRow(summary, 17, "Execution Result", request.ExecutionResult ?? "", navy);
        AddWorkbookRow(summary, 18, "Execution Error", request.ExecutionError ?? "", navy);
        AddWorkbookRow(summary, 19, "Parameters", FormatParameters(request.ParametersJson), navy);
        AddWorkbookRow(summary, 20, "Original Parameters", FormatParameters(request.OriginalParametersJson ?? request.ParametersJson), navy);
        summary.Columns().AdjustToContents();

        var approvals = wb.Worksheets.Add("Approvals");
        WriteHeader(approvals, ["Time UTC", "Approver", "Decision", "Note"], navy);
        var row = 2;
        foreach (var approval in request.Approvals.OrderBy(a => a.OccurredAt))
        {
            approvals.Cell(row, 1).Value = FormatUtc(approval.OccurredAt);
            approvals.Cell(row, 2).Value = approval.ApproverUpn;
            approvals.Cell(row, 3).Value = approval.Approved ? "Approved" : "Denied";
            approvals.Cell(row, 4).Value = approval.Note ?? "";
            row++;
        }
        approvals.Columns().AdjustToContents();

        var commentSheet = wb.Worksheets.Add("Comments");
        WriteHeader(commentSheet, ["Time UTC", "Role", "Author", "Display Name", "Comment"], navy);
        row = 2;
        foreach (var comment in comments)
        {
            commentSheet.Cell(row, 1).Value = FormatUtc(comment.CreatedAt);
            commentSheet.Cell(row, 2).Value = comment.IsAdmin ? "Approver" : "Requester";
            commentSheet.Cell(row, 3).Value = comment.AuthorUpn;
            commentSheet.Cell(row, 4).Value = comment.AuthorDisplayName;
            commentSheet.Cell(row, 5).Value = comment.Body;
            row++;
        }
        commentSheet.Columns().AdjustToContents();

        var audit = wb.Worksheets.Add("Audit Events");
        WriteHeader(audit, ["Time UTC", "Event", "Actor", "Machine", "Detail", "Previous Hash", "Row Hash"], navy);
        row = 2;
        foreach (var entry in auditEntries)
        {
            audit.Cell(row, 1).Value = FormatUtc(entry.OccurredAt);
            audit.Cell(row, 2).Value = entry.Event;
            audit.Cell(row, 3).Value = entry.ActorUpn;
            audit.Cell(row, 4).Value = entry.TargetMachine ?? "";
            audit.Cell(row, 5).Value = entry.Detail ?? "";
            audit.Cell(row, 6).Value = entry.PreviousHash ?? "";
            audit.Cell(row, 7).Value = entry.RowHash ?? "";
            row++;
        }
        audit.Columns().AdjustToContents();

        var info = wb.Worksheets.Add("Export Info");
        AddWorkbookRow(info, 1, "Export Generated UTC", FormatUtc(exportedAt), navy);
        AddWorkbookRow(info, 2, "Bundle Type", "Request Evidence", navy);
        AddWorkbookRow(info, 3, "Audit Entries Included", auditEntries.Count.ToString(), navy);
        AddWorkbookRow(info, 4, "Comments Included", comments.Count.ToString(), navy);
        AddWorkbookRow(info, 5, "Security Note", "No endpoint API keys, API key hashes, private signing keys, or SMTP secrets are included.", navy);
        info.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static byte[] BuildEndpointDiagnosticsWorkbook(
        List<EndpointRegistration> endpoints,
        List<ElevationRequest> recentRequests,
        List<AuditLog> recentAudit,
        string? latestAgentVersion,
        DateTimeOffset exportedAt)
    {
        using var wb = new XLWorkbook();
        var navy = XLColor.FromHtml("#1A2B4B");

        var endpointSheet = wb.Worksheets.Add("Endpoints");
        WriteHeader(endpointSheet, [
            "Health",
            "Machine",
            "Enabled",
            "Online",
            "Agent Version",
            "Latest Version",
            "Outdated",
            "OS",
            "Last Seen UTC",
            "Last Poll UTC",
            "Poll Signing",
            "Last Poll Error",
            "Update Pending",
            "Last Update Status",
            "Last Update Error",
            "Registered UTC"
        ], navy);

        var row = 2;
        foreach (var ep in endpoints)
        {
            endpointSheet.Cell(row, 1).Value = EndpointHealth(ep);
            endpointSheet.Cell(row, 2).Value = ep.MachineName;
            endpointSheet.Cell(row, 3).Value = ep.IsEnabled ? "Yes" : "No";
            endpointSheet.Cell(row, 4).Value = IsOnline(ep) ? "Yes" : "No";
            endpointSheet.Cell(row, 5).Value = ep.AgentVersion ?? "";
            endpointSheet.Cell(row, 6).Value = latestAgentVersion ?? "";
            endpointSheet.Cell(row, 7).Value = IsOutdated(ep, latestAgentVersion) ? "Yes" : "No";
            endpointSheet.Cell(row, 8).Value = ep.OsVersion ?? "";
            endpointSheet.Cell(row, 9).Value = FormatUtc(ep.LastSeenAt);
            endpointSheet.Cell(row, 10).Value = FormatUtc(ep.LastPollSucceededAt);
            endpointSheet.Cell(row, 11).Value = ep.PollSigningConfigured ? "Configured" : "Missing";
            endpointSheet.Cell(row, 12).Value = ep.LastPollError ?? "";
            endpointSheet.Cell(row, 13).Value = ep.UpdatePending ? "Yes" : "No";
            endpointSheet.Cell(row, 14).Value = ep.LastUpdateStatus ?? "";
            endpointSheet.Cell(row, 15).Value = ep.LastUpdateError ?? "";
            endpointSheet.Cell(row, 16).Value = FormatUtc(ep.RegisteredAt);
            row++;
        }
        endpointSheet.Columns().AdjustToContents();

        var requestSheet = wb.Worksheets.Add("Recent Requests");
        WriteHeader(requestSheet, ["Submitted UTC", "Request ID", "Machine", "Action", "Status", "Requester", "Reviewer", "Executed UTC"], navy);
        row = 2;
        foreach (var request in recentRequests)
        {
            requestSheet.Cell(row, 1).Value = FormatUtc(request.SubmittedAt);
            requestSheet.Cell(row, 2).Value = request.Id.ToString();
            requestSheet.Cell(row, 3).Value = request.Endpoint?.MachineName ?? "";
            requestSheet.Cell(row, 4).Value = request.ActionDefinition?.DisplayName ?? "";
            requestSheet.Cell(row, 5).Value = request.Status.ToString();
            requestSheet.Cell(row, 6).Value = request.RequesterUpn;
            requestSheet.Cell(row, 7).Value = request.ReviewerUpn ?? "";
            requestSheet.Cell(row, 8).Value = FormatUtc(request.ExecutedAt);
            row++;
        }
        requestSheet.Columns().AdjustToContents();

        var auditSheet = wb.Worksheets.Add("Recent Audit");
        WriteHeader(auditSheet, ["Time UTC", "Event", "Actor", "Machine", "Request ID", "Detail"], navy);
        row = 2;
        foreach (var entry in recentAudit)
        {
            auditSheet.Cell(row, 1).Value = FormatUtc(entry.OccurredAt);
            auditSheet.Cell(row, 2).Value = entry.Event;
            auditSheet.Cell(row, 3).Value = entry.ActorUpn;
            auditSheet.Cell(row, 4).Value = entry.TargetMachine ?? "";
            auditSheet.Cell(row, 5).Value = entry.RequestId?.ToString() ?? "";
            auditSheet.Cell(row, 6).Value = entry.Detail ?? "";
            row++;
        }
        auditSheet.Columns().AdjustToContents();

        var info = wb.Worksheets.Add("Export Info");
        AddWorkbookRow(info, 1, "Export Generated UTC", FormatUtc(exportedAt), navy);
        AddWorkbookRow(info, 2, "Bundle Type", "Endpoint Diagnostics", navy);
        AddWorkbookRow(info, 3, "Endpoint Count", endpoints.Count.ToString(), navy);
        AddWorkbookRow(info, 4, "Latest Agent Version", latestAgentVersion ?? "", navy);
        AddWorkbookRow(info, 5, "Security Note", "No endpoint API keys, API key hashes, private signing keys, or SMTP secrets are included.", navy);
        info.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

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

    private static Dictionary<string, string> DeserializeParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return [];

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson, JsonOpts) ?? [];
            return dict
                .Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch
        {
            return [];
        }
    }

    private static List<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];

        try { return JsonSerializer.Deserialize<List<string>>(tagsJson, JsonOpts) ?? []; }
        catch { return []; }
    }

    private static object RequestEvidence(ElevationRequest request) => new
    {
        request.Id,
        Status = request.Status.ToString(),
        ActionDefinitionId = request.ActionDefinitionId,
        ActionDisplayName = request.ActionDefinition?.DisplayName ?? "",
        ActionType = request.ActionDefinition?.ActionType.ToString() ?? "",
        request.RequesterUpn,
        request.RequesterDisplayName,
        request.Justification,
        request.TicketReference,
        SubmittedAtUtc = request.SubmittedAt,
        ExpiresAtUtc = request.ExpiresAt,
        ReviewedAtUtc = request.ReviewedAt,
        ExecutedAtUtc = request.ExecutedAt,
        ScheduledForUtc = request.ScheduledForUtc,
        request.ReviewerUpn,
        request.ReviewerNote,
        request.ExecutionResult,
        request.ExecutionError,
        Endpoint = request.Endpoint is null ? null : new
        {
            request.Endpoint.Id,
            request.Endpoint.MachineName,
            request.Endpoint.MachineId,
            request.Endpoint.AgentVersion,
            request.Endpoint.OsVersion,
        },
        Parameters = DeserializeParameters(request.ParametersJson),
        OriginalParameters = DeserializeParameters(request.OriginalParametersJson ?? request.ParametersJson),
    };

    private static object? EndpointHealthEvidence(EndpointRegistration? endpoint, string? latestAgentVersion = null)
    {
        if (endpoint is null) return null;

        return new
        {
            endpoint.Id,
            endpoint.MachineName,
            endpoint.MachineId,
            endpoint.Description,
            endpoint.IsEnabled,
            Online = IsOnline(endpoint),
            Health = EndpointHealth(endpoint),
            endpoint.RegisteredAt,
            endpoint.LastSeenAt,
            endpoint.OsVersion,
            endpoint.AgentVersion,
            LatestAgentVersion = latestAgentVersion,
            Outdated = IsOutdated(endpoint, latestAgentVersion),
            Tags = DeserializeTags(endpoint.TagsJson),
            endpoint.DormantSince,
            endpoint.UpdatePending,
            endpoint.UpdateRequestedAt,
            endpoint.PollSigningConfigured,
            endpoint.LastPollSucceededAt,
            endpoint.LastPollError,
            endpoint.LastUpdateCheckAt,
            endpoint.LastUpdateStatus,
            endpoint.LastUpdateError,
            endpoint.LastSuccessfulUpdateAt,
        };
    }

    private static object AuditEvidence(AuditLog entry) => new
    {
        entry.Id,
        entry.RequestId,
        entry.Event,
        entry.ActorUpn,
        entry.TargetMachine,
        entry.Detail,
        entry.IpAddress,
        OccurredAtUtc = entry.OccurredAt,
        entry.PreviousHash,
        entry.RowHash,
    };

    private static bool IsOnline(EndpointRegistration endpoint) =>
        endpoint.IsEnabled &&
        endpoint.LastSeenAt.HasValue &&
        (DateTimeOffset.UtcNow - endpoint.LastSeenAt.Value).TotalMinutes < 10;

    private static bool HasHealthIssue(EndpointRegistration endpoint) =>
        !endpoint.PollSigningConfigured ||
        !string.IsNullOrWhiteSpace(endpoint.LastPollError) ||
        !string.IsNullOrWhiteSpace(endpoint.LastUpdateError) ||
        !endpoint.IsEnabled;

    private static bool IsOutdated(EndpointRegistration endpoint, string? latestAgentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestAgentVersion) || string.IsNullOrWhiteSpace(endpoint.AgentVersion))
            return false;

        return Version.TryParse(endpoint.AgentVersion, out var endpointVersion)
            && Version.TryParse(latestAgentVersion, out var latestVersion)
            && endpointVersion < latestVersion;
    }

    private static string EndpointHealth(EndpointRegistration endpoint)
    {
        if (!endpoint.IsEnabled)
            return endpoint.DormantSince.HasValue ? "Dormant" : "Disabled";
        if (!endpoint.PollSigningConfigured)
            return "Missing poll signing key";
        if (!string.IsNullOrWhiteSpace(endpoint.LastPollError))
            return "Poll failing";
        if (!string.IsNullOrWhiteSpace(endpoint.LastUpdateError))
            return "Update failing";
        if (endpoint.LastSeenAt is null)
            return "Never seen";
        if (!IsOnline(endpoint))
            return "Offline";
        return "Healthy";
    }

    private static void AddJson(ZipArchive zip, string path, object value)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions(JsonOpts) { WriteIndented = true });
    }

    private static void AddBytes(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteHeader(IXLWorksheet ws, string[] headers, XLColor headerColor)
    {
        for (var i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        var row = ws.Row(1);
        row.Style.Font.Bold = true;
        row.Style.Font.FontColor = XLColor.White;
        row.Style.Fill.BackgroundColor = headerColor;
        row.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.SheetView.FreezeRows(1);
    }

    private static void AddWorkbookRow(IXLWorksheet ws, int row, string label, string value, XLColor labelColor)
    {
        ws.Cell(row, 1).Value = label;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(row, 1).Style.Fill.BackgroundColor = labelColor;
        ws.Cell(row, 2).Value = value;
    }

    private static string FormatUtc(DateTimeOffset? value) =>
        value.HasValue ? FormatUtc(value.Value) : string.Empty;

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    private static string SanitiseFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Endpoint";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "Endpoint" : safe;
    }
}
