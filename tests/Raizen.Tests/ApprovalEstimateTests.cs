using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Endpoint.Tray.Forms;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Licensing;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Tests for the estimated approval wait time feature:
///   - Server-side: ActionCatalogService.ListAsync enriches DTOs with AverageApprovalSeconds
///   - Client-side: RequestForm.FormatApprovalEstimate formats the display string
/// </summary>
public sealed class ApprovalEstimateTests : IDisposable
{
    private readonly DbContextOptions<RaizenDbContext> _dbOpts;
    private readonly RaizenDbContext _db;
    private readonly ActionCatalogService _catalog;

    public ApprovalEstimateTests()
    {
        _dbOpts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"ApprovalEstimate_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(_dbOpts);

        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Estimate",
            })
            .Build();

        var syslog = new NullSyslogSender();
        var audit = new AuditService(new TestDbContextFactory(_dbOpts), syslog, cfg);
        _catalog = new ActionCatalogService(new TestDbContextFactory(_dbOpts), audit, new AllFeaturesLicenseStub());
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ActionDefinition SeedAction(string name = "Test Action")
    {
        var def = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            ActionType = ActionType.StartService,
            ApprovalWindowMinutes = 60,
            MinApprovers = 1,
            IsEnabled = true,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        _db.ActionDefinitions.Add(def);
        return def;
    }

    private EndpointRegistration SeedEndpoint()
    {
        var ep = new EndpointRegistration
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString(),
            MachineName = "TESTPC",
            ApiKeyHash = new string('0', 128),
            IsEnabled = true,
        };
        _db.EndpointRegistrations.Add(ep);
        return ep;
    }

    private void SeedRequest(ActionDefinition def, EndpointRegistration ep,
        TimeSpan approvalDuration, string reviewerUpn = "approver@corp.com")
    {
        var submitted = DateTimeOffset.UtcNow.AddHours(-1);
        _db.ElevationRequests.Add(new ElevationRequest
        {
            Id = Guid.NewGuid(),
            ActionDefinitionId = def.Id,
            EndpointRegistrationId = ep.Id,
            RequesterUpn = "user@corp.com",
            Justification = "test",
            Status = RequestStatus.Approved,
            SubmittedAt = submitted,
            ReviewedAt = submitted + approvalDuration,
            ExpiresAt = submitted.AddHours(2),
            ReviewerUpn = reviewerUpn,
        });
    }

    // =====================================================================
    // Server-side: ActionCatalogService computes AverageApprovalSeconds
    // =====================================================================

    [Fact]
    public async Task ListAsync_WithEnoughRequests_ReturnsAverage()
    {
        var def = SeedAction();
        var ep = SeedEndpoint();

        // 3 requests with 5, 10, 15 minute approval times → avg = 10 min = 600 sec
        SeedRequest(def, ep, TimeSpan.FromMinutes(5));
        SeedRequest(def, ep, TimeSpan.FromMinutes(10));
        SeedRequest(def, ep, TimeSpan.FromMinutes(15));
        await _db.SaveChangesAsync();

        var result = await _catalog.ListAsync(ct: default);
        var action = result.Single();

        Assert.NotNull(action.AverageApprovalSeconds);
        // Allow 1-second tolerance for floating point
        Assert.InRange(action.AverageApprovalSeconds!.Value, 598, 602);
    }

    [Fact]
    public async Task ListAsync_FewerThan3Requests_ReturnsNull()
    {
        var def = SeedAction();
        var ep = SeedEndpoint();

        SeedRequest(def, ep, TimeSpan.FromMinutes(5));
        SeedRequest(def, ep, TimeSpan.FromMinutes(10));
        await _db.SaveChangesAsync();

        var result = await _catalog.ListAsync(ct: default);
        Assert.Null(result.Single().AverageApprovalSeconds);
    }

    [Fact]
    public async Task ListAsync_NoRequests_ReturnsNull()
    {
        SeedAction();
        await _db.SaveChangesAsync();

        var result = await _catalog.ListAsync(ct: default);
        Assert.Null(result.Single().AverageApprovalSeconds);
    }

    [Fact]
    public async Task ListAsync_ExcludesAutoApproved()
    {
        var def = SeedAction();
        var ep = SeedEndpoint();

        // 3 auto-approved (should be excluded)
        SeedRequest(def, ep, TimeSpan.FromSeconds(1), "system:auto-approve");
        SeedRequest(def, ep, TimeSpan.FromSeconds(1), "system:auto-approve");
        SeedRequest(def, ep, TimeSpan.FromSeconds(1), "system:auto-approve");

        // Only 2 manual (below threshold)
        SeedRequest(def, ep, TimeSpan.FromMinutes(5));
        SeedRequest(def, ep, TimeSpan.FromMinutes(10));
        await _db.SaveChangesAsync();

        var result = await _catalog.ListAsync(ct: default);
        // Only 2 manual requests, below the 3 threshold → null
        Assert.Null(result.Single().AverageApprovalSeconds);
    }

    [Fact]
    public async Task ListAsync_MultipleActions_IndependentAverages()
    {
        var fastAction = SeedAction("Fast Action");
        var slowAction = SeedAction("Slow Action");
        var ep = SeedEndpoint();

        // Fast: 1, 2, 3 min → avg ~2 min = ~120 sec
        SeedRequest(fastAction, ep, TimeSpan.FromMinutes(1));
        SeedRequest(fastAction, ep, TimeSpan.FromMinutes(2));
        SeedRequest(fastAction, ep, TimeSpan.FromMinutes(3));

        // Slow: 20, 30, 40 min → avg 30 min = 1800 sec
        SeedRequest(slowAction, ep, TimeSpan.FromMinutes(20));
        SeedRequest(slowAction, ep, TimeSpan.FromMinutes(30));
        SeedRequest(slowAction, ep, TimeSpan.FromMinutes(40));
        await _db.SaveChangesAsync();

        var result = await _catalog.ListAsync(ct: default);
        var fast = result.Single(a => a.DisplayName == "Fast Action");
        var slow = result.Single(a => a.DisplayName == "Slow Action");

        Assert.InRange(fast.AverageApprovalSeconds!.Value, 118, 122);
        Assert.InRange(slow.AverageApprovalSeconds!.Value, 1798, 1802);
    }

    // =====================================================================
    // Client-side: FormatApprovalEstimate
    // =====================================================================

    [Fact]
    public void Format_AutoApprove_ShowsAutoApproved()
    {
        var action = new ActionDefinitionDto { AutoApprove = true, AverageApprovalSeconds = 300 };
        Assert.Equal("This action is auto-approved", RequestForm.FormatApprovalEstimate(action));
    }

    [Fact]
    public void Format_NullAverage_ReturnsEmpty()
    {
        var action = new ActionDefinitionDto { AverageApprovalSeconds = null };
        Assert.Equal("", RequestForm.FormatApprovalEstimate(action));
    }

    [Fact]
    public void Format_LessThan60Seconds_ShowsLessThan1Min()
    {
        var action = new ActionDefinitionDto { AverageApprovalSeconds = 30 };
        Assert.Equal("Avg. approval time: < 1 min", RequestForm.FormatApprovalEstimate(action));
    }

    [Fact]
    public void Format_6Minutes_Shows6Min()
    {
        var action = new ActionDefinitionDto { AverageApprovalSeconds = 360 };
        Assert.Equal("Avg. approval time: ~6 min", RequestForm.FormatApprovalEstimate(action));
    }

    [Fact]
    public void Format_OverOneHour_ShowsMoreThan1Hour()
    {
        var action = new ActionDefinitionDto { AverageApprovalSeconds = 4000 };
        Assert.Equal("Avg. approval time: > 1 hour", RequestForm.FormatApprovalEstimate(action));
    }

    [Fact]
    public void Format_Exactly60Seconds_Shows1Min()
    {
        var action = new ActionDefinitionDto { AverageApprovalSeconds = 60 };
        Assert.Equal("Avg. approval time: ~1 min", RequestForm.FormatApprovalEstimate(action));
    }
}
