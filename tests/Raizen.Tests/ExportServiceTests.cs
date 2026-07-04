using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Tests for ExportService — verifies ISO 27001 Excel export correctness
/// across the three selectable time windows (3 months, 6 months, 1 year).
/// Uses EF Core InMemory provider; no real PostgreSQL required.
/// </summary>
public sealed class ExportServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TestDbContextFactory CreateDbFactory()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContextFactory(opts);
    }

    private static (ActionDefinition action, EndpointRegistration endpoint)
        SeedBase(RaizenDbContext db)
    {
        var action = new ActionDefinition
        {
            Id          = Guid.NewGuid(),
            DisplayName = "Test Action",
            ActionType  = ActionType.CopyFile,
            IsEnabled   = true,
        };
        var endpoint = new EndpointRegistration
        {
            Id          = Guid.NewGuid(),
            MachineId   = "machine-001",
            MachineName = "WORKSTATION01",
            ApiKeyHash  = "hash",
        };
        db.ActionDefinitions.Add(action);
        db.EndpointRegistrations.Add(endpoint);
        db.SaveChanges();
        return (action, endpoint);
    }

    private static ElevationRequest MakeRequest(
        ActionDefinition action,
        EndpointRegistration endpoint,
        DateTimeOffset submittedAt,
        string requesterUpn    = "alice@contoso.com",
        string reviewerUpn     = "bob@contoso.com",
        RequestStatus status   = RequestStatus.Succeeded)
    {
        return new ElevationRequest
        {
            Id                     = Guid.NewGuid(),
            ActionDefinitionId     = action.Id,
            ActionDefinition       = action,
            EndpointRegistrationId = endpoint.Id,
            Endpoint               = endpoint,
            RequesterUpn           = requesterUpn,
            RequesterDisplayName   = "Alice Smith",
            Justification          = "Unit test justification",
            TicketReference        = "TICKET-42",
            ParametersJson         = """{"SourcePath":"C:\\test.txt","DestinationPath":"C:\\dest.txt"}""",
            Status                 = status,
            SubmittedAt            = submittedAt,
            ExpiresAt              = submittedAt.AddHours(1),
            ReviewedAt             = submittedAt.AddMinutes(5),
            ReviewerUpn            = reviewerUpn,
            ReviewerNote           = "Approved in test",
            ExecutedAt             = submittedAt.AddMinutes(10),
            ExecutionResult        = "Copy succeeded",
        };
    }

    private static IXLWorksheet OpenSheet(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var wb = new XLWorkbook(ms);
        return wb.Worksheet("Elevation Requests");
    }

    private static int DataRowCount(byte[] bytes)
    {
        var ws = OpenSheet(bytes);
        // Row 1 = header; count rows that have a non-empty Request ID (col 1)
        return ws.RowsUsed().Skip(1).Count(r => !string.IsNullOrWhiteSpace(r.Cell(1).GetString()));
    }

    // ── Test 1: 3-month period only returns recent records ────────────────────

    [Fact]
    public async Task Export_3Months_ExcludesOlderRequests()
    {
        var factory = CreateDbFactory();
        using var db = factory.CreateDbContext();
        var (action, endpoint) = SeedBase(db);

        var now = DateTimeOffset.UtcNow;

        // 1 old request (5 months ago) — should be excluded from 3M export
        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-5), "old@contoso.com"));
        // 2 recent requests (1 month ago) — should be included
        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-1), "alice@contoso.com"));
        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-1), "carol@contoso.com"));
        await db.SaveChangesAsync();

        var svc   = new ExportService(factory);
        var bytes = await svc.ExportRequestsAsync(now.AddMonths(-3));

        Assert.Equal(2, DataRowCount(bytes));
    }

    // ── Test 2: 6-month period includes all three records ─────────────────────

    [Fact]
    public async Task Export_6Months_IncludesAllThreeRequests()
    {
        var factory = CreateDbFactory();
        using var db = factory.CreateDbContext();
        var (action, endpoint) = SeedBase(db);

        var now = DateTimeOffset.UtcNow;

        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-5), "old@contoso.com"));
        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-1), "alice@contoso.com"));
        db.ElevationRequests.Add(MakeRequest(action, endpoint, now.AddMonths(-1), "carol@contoso.com"));
        await db.SaveChangesAsync();

        var svc   = new ExportService(factory);
        var bytes = await svc.ExportRequestsAsync(now.AddMonths(-6));

        Assert.Equal(3, DataRowCount(bytes));
    }

    // ── Test 3: 1-year period — verify column layout and cell values ──────────

    [Fact]
    public async Task Export_1Year_ColumnHeadersAndValuesAreCorrect()
    {
        var factory = CreateDbFactory();
        using var db = factory.CreateDbContext();
        var (action, endpoint) = SeedBase(db);

        var submittedAt = DateTimeOffset.UtcNow.AddMonths(-2);
        var requestId   = Guid.NewGuid();
        var req = MakeRequest(action, endpoint, submittedAt);
        req.Id = requestId;
        db.ElevationRequests.Add(req);
        await db.SaveChangesAsync();

        var svc   = new ExportService(factory);
        var bytes = await svc.ExportRequestsAsync(DateTimeOffset.UtcNow.AddYears(-1));

        using var ms = new MemoryStream(bytes);
        var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("Elevation Requests");

        // ── Header row checks ────────────────────────────────────────────────
        Assert.Equal("Request ID",          ws.Cell(1, 1).GetString());
        Assert.Equal("Status",              ws.Cell(1, 2).GetString());
        Assert.Equal("Action Name",         ws.Cell(1, 3).GetString());
        Assert.Equal("Action Type",         ws.Cell(1, 4).GetString());
        Assert.Equal("Target Machine",      ws.Cell(1, 5).GetString());
        Assert.Equal("Requester UPN",       ws.Cell(1, 7).GetString());
        Assert.Equal("Reviewer UPN",        ws.Cell(1, 13).GetString());
        Assert.Equal("Review Decision",     ws.Cell(1, 14).GetString());
        Assert.Equal("Parameters",          ws.Cell(1, 21).GetString());

        // ── Data row checks ──────────────────────────────────────────────────
        Assert.Equal(requestId.ToString(),  ws.Cell(2, 1).GetString());
        Assert.Equal("Succeeded",           ws.Cell(2, 2).GetString());
        Assert.Equal("Test Action",         ws.Cell(2, 3).GetString());
        Assert.Equal("CopyFile",            ws.Cell(2, 4).GetString());
        Assert.Equal("WORKSTATION01",       ws.Cell(2, 5).GetString());
        Assert.Equal("alice@contoso.com",   ws.Cell(2, 7).GetString());
        Assert.Equal("bob@contoso.com",     ws.Cell(2, 13).GetString());
        Assert.Equal("Approved",            ws.Cell(2, 14).GetString());

        // Parameters should contain the keys (not the __ prefixed internal ones)
        var parameters = ws.Cell(2, 21).GetString();
        Assert.Contains("SourcePath",       parameters);
        Assert.Contains("DestinationPath",  parameters);

        // ── Export Info sheet must exist ──────────────────────────────────────
        Assert.NotNull(wb.Worksheet("Export Info"));
        Assert.Equal("1", wb.Worksheet("Export Info").Cell(4, 2).GetString());
    }

    // ── Test 4: Empty period returns valid workbook with zero data rows ────────

    [Fact]
    public async Task Export_EmptyDatabase_ReturnsWorkbookWithHeaderOnly()
    {
        var factory = CreateDbFactory();
        using var db = factory.CreateDbContext();
        var svc      = new ExportService(factory);
        var bytes     = await svc.ExportRequestsAsync(DateTimeOffset.UtcNow.AddMonths(-3));

        Assert.True(bytes.Length > 0, "Should return non-empty XLSX even with no data.");
        Assert.Equal(0, DataRowCount(bytes));

        // Header row must still be present
        using var ms = new MemoryStream(bytes);
        var wb = new XLWorkbook(ms);
        Assert.Equal("Request ID", wb.Worksheet("Elevation Requests").Cell(1, 1).GetString());
    }
}
