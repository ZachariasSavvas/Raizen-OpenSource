using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Licensing;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Tests for open-source entitlement behavior:
///   - legacy tier strings all map to the unrestricted edition
///   - all product features are available
///   - ActionCatalogService preserves MinApprovers without tier clamps
/// </summary>
public sealed class LicenseTierTests : IDisposable
{
    private readonly DbContextOptions<RaizenDbContext> _dbOpts;
    private readonly RaizenDbContext _db;

    public LicenseTierTests()
    {
        _dbOpts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"LicenseTier_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(_dbOpts);
    }

    public void Dispose() => _db.Dispose();

    // =====================================================================
    // ParseTier
    // =====================================================================

    [Theory]
    [InlineData("Starter", LicenseTier.Enterprise)]
    [InlineData("Business", LicenseTier.Enterprise)]
    [InlineData("Enterprise", LicenseTier.Enterprise)]
    public void ParseTier_KnownValues(string input, LicenseTier expected)
    {
        Assert.Equal(expected, LicenseService.ParseTier(input));
    }

    [Fact]
    public void ParseTier_Professional_MapsToBusiness()
    {
        Assert.Equal(LicenseTier.Enterprise, LicenseService.ParseTier("Professional"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("FooBar")]
    [InlineData("trial")]
    public void ParseTier_InvalidOrEmpty_MapsToTrial(string? input)
    {
        Assert.Equal(LicenseTier.Enterprise, LicenseService.ParseTier(input));
    }

    // =====================================================================
    // Feature gating per tier
    // =====================================================================

    private static readonly LicenseFeature[] AllBusinessFeatures =
    [
        LicenseFeature.AutoApprovalRules,
        LicenseFeature.ExcelExport,
        LicenseFeature.AgentAutoUpdate,
        LicenseFeature.SyslogForwarding,
        LicenseFeature.RegistrationTokens,
        LicenseFeature.MultiApprover,
    ];

    private static readonly LicenseFeature[] EnterpriseOnlyFeatures =
    [
        LicenseFeature.RealtimeToasts,
    ];

    [Fact]
    public void HasFeature_TrialTier_AllGatedFeaturesFalse()
    {
        var svc = new StubLicenseService(LicenseTier.Trial);
        foreach (var feature in AllBusinessFeatures.Concat(EnterpriseOnlyFeatures))
            Assert.True(svc.HasFeature(feature), $"Open-source edition should have {feature}");
    }

    [Fact]
    public void HasFeature_StarterTier_AllGatedFeaturesFalse()
    {
        var svc = new StubLicenseService(LicenseTier.Starter);
        foreach (var feature in AllBusinessFeatures.Concat(EnterpriseOnlyFeatures))
            Assert.True(svc.HasFeature(feature), $"Open-source edition should have {feature}");
    }

    [Fact]
    public void HasFeature_BusinessTier_BusinessFeaturesTrue_EnterpriseFalse()
    {
        var svc = new StubLicenseService(LicenseTier.Business);
        foreach (var feature in AllBusinessFeatures)
            Assert.True(svc.HasFeature(feature), $"Business should have {feature}");
        foreach (var feature in EnterpriseOnlyFeatures)
            Assert.True(svc.HasFeature(feature), $"Open-source edition should have {feature}");
    }

    [Fact]
    public void HasFeature_EnterpriseTier_AllFeaturesTrue()
    {
        var svc = new StubLicenseService(LicenseTier.Enterprise);
        foreach (var feature in AllBusinessFeatures.Concat(EnterpriseOnlyFeatures))
            Assert.True(svc.HasFeature(feature), $"Enterprise should have {feature}");
    }

    // =====================================================================
    // MinApprovers enforcement in ActionCatalogService
    // =====================================================================

    [Fact]
    public async Task CreateAsync_MinApproversClampedToOne_WhenTierBelowBusiness()
    {
        var factory = new TestDbContextFactory(_dbOpts);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Tier",
            })
            .Build();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var license = new StubLicenseService(LicenseTier.Starter); // No MultiApprover

        var catalog = new ActionCatalogService(factory, audit, license);

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test Action",
            ActionType = ActionType.StartService,
            MinApprovers = 3,
            IsEnabled = true,
            ApprovalWindowMinutes = 60,
        };

        var result = await catalog.CreateAsync(dto, "test@corp.com");

        Assert.Equal(3, result.MinApprovers);
    }

    [Fact]
    public async Task CreateAsync_MinApproversPreserved_WhenTierBusiness()
    {
        var factory = new TestDbContextFactory(_dbOpts);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Tier2",
            })
            .Build();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var license = new StubLicenseService(LicenseTier.Business); // Has MultiApprover

        var catalog = new ActionCatalogService(factory, audit, license);

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test Action 2",
            ActionType = ActionType.StartService,
            MinApprovers = 3,
            IsEnabled = true,
            ApprovalWindowMinutes = 60,
        };

        var result = await catalog.CreateAsync(dto, "test@corp.com");

        // Should preserve 3 since Business has MultiApprover
        Assert.Equal(3, result.MinApprovers);
    }

    [Fact]
    public async Task UpdateAsync_MinApproversClampedToOne_WhenTierBelowBusiness()
    {
        var factory = new TestDbContextFactory(_dbOpts);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Tier3",
            })
            .Build();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);

        // First create with Business tier
        var businessLicense = new StubLicenseService(LicenseTier.Business);
        var catalog = new ActionCatalogService(factory, audit, businessLicense);

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Update Test",
            ActionType = ActionType.StartService,
            MinApprovers = 2,
            IsEnabled = true,
            ApprovalWindowMinutes = 60,
        };
        var created = await catalog.CreateAsync(dto, "test@corp.com");
        Assert.Equal(2, created.MinApprovers);

        // Now update with Starter tier (downgraded)
        var starterLicense = new StubLicenseService(LicenseTier.Starter);
        var catalogDowngraded = new ActionCatalogService(factory, audit, starterLicense);

        dto.MinApprovers = 4;
        var updated = await catalogDowngraded.UpdateAsync(created.Id, dto, "test@corp.com");

        Assert.Equal(4, updated.MinApprovers);
    }

    // =====================================================================
    // End-to-end: Multi-Approver flow through RequestService.ReviewAsync
    // =====================================================================

    private (RequestService requestService, ActionCatalogService catalog) BuildServices(LicenseTier tier)
    {
        var factory = new TestDbContextFactory(_dbOpts);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_E2E",
                ["RequestLimits:MaxPerUserPerHour"] = "100",
            })
            .Build();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var license = new StubLicenseService(tier);
        var catalog = new ActionCatalogService(factory, audit, license);
        var notifications = new StubNotificationService();
        var autoApproval = new StubAutoApprovalService();
        var requestService = new RequestService(factory, audit, catalog, notifications, autoApproval, cfg);
        return (requestService, catalog);
    }

    private async Task<EndpointRegistration> SeedEndpointAsync()
    {
        var ep = new EndpointRegistration
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString(),
            MachineName = "TESTPC",
            ApiKeyHash = new string('0', 128),
            IsEnabled = true,
        };
        await using var db = new RaizenDbContext(_dbOpts);
        db.EndpointRegistrations.Add(ep);
        await db.SaveChangesAsync();
        return ep;
    }

    private async Task<ActionDefinition> SeedActionAsync(int minApprovers = 1)
    {
        var def = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = "Dual Approval Action",
            ActionType = ActionType.StartService,
            ApprovalWindowMinutes = 60,
            MinApprovers = minApprovers,
            IsEnabled = true,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        // Use a fresh context (same as services will use) to avoid tracking conflicts
        await using var db = new RaizenDbContext(_dbOpts);
        db.ActionDefinitions.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    [Fact]
    public async Task MultiApprover_TwoApproversRequired_FirstApprovalStaysPending()
    {
        var (svc, _) = BuildServices(LicenseTier.Business);
        var ep = await SeedEndpointAsync();
        var def = await SeedActionAsync(minApprovers: 2);

        // Submit request
        var request = await svc.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification = "Need dual approval",
                Parameters = new(),
            },
            "requester@corp.com", "Requester", ep.Id);

        Assert.Equal(RequestStatus.Pending, request.Status);

        // First approval — should stay Pending (need 2)
        var afterFirst = await svc.ReviewAsync(
            request.Id,
            new ReviewRequestDto { Approved = true, Note = "LGTM from approver 1" },
            "approver1@corp.com");

        Assert.Equal(RequestStatus.Pending, afterFirst.Status);
    }

    [Fact]
    public async Task MultiApprover_TwoApproversRequired_SecondApprovalApprovesRequest()
    {
        var (svc, _) = BuildServices(LicenseTier.Business);
        var ep = await SeedEndpointAsync();
        var def = await SeedActionAsync(minApprovers: 2);

        var request = await svc.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification = "Need dual approval",
                Parameters = new(),
            },
            "requester@corp.com", "Requester", ep.Id);

        // First approval
        await svc.ReviewAsync(
            request.Id,
            new ReviewRequestDto { Approved = true, Note = "LGTM 1" },
            "approver1@corp.com");

        // Second approval — should transition to Approved
        var afterSecond = await svc.ReviewAsync(
            request.Id,
            new ReviewRequestDto { Approved = true, Note = "LGTM 2" },
            "approver2@corp.com");

        Assert.Equal(RequestStatus.Approved, afterSecond.Status);
    }

    [Fact]
    public async Task MultiApprover_SameApproverTwice_Throws()
    {
        var (svc, _) = BuildServices(LicenseTier.Business);
        var ep = await SeedEndpointAsync();
        var def = await SeedActionAsync(minApprovers: 2);

        var request = await svc.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification = "test",
                Parameters = new(),
            },
            "requester@corp.com", "Requester", ep.Id);

        // First approval
        await svc.ReviewAsync(
            request.Id,
            new ReviewRequestDto { Approved = true, Note = "LGTM" },
            "approver1@corp.com");

        // Same approver tries again — must throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReviewAsync(
                request.Id,
                new ReviewRequestDto { Approved = true, Note = "again" },
                "approver1@corp.com"));

        Assert.Contains("already approved", ex.Message);
    }

    // ── Stubs ───────────────────────────────────────────────────────────────

    private sealed class StubNotificationService : INotificationService
    {
        public Task<NotificationSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new NotificationSettings());
        public Task SaveAsync(NotificationSettings settings, string actorUpn, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> TestAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task SendRequestSubmittedAsync(ElevationRequestDto request, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SendRequestReviewedAsync(ElevationRequestDto request, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAutoApprovalService : IAutoApprovalService
    {
        public Task<bool> ShouldAutoApproveAsync(ActionType actionType, Guid actionDefinitionId, string requesterUpn, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<List<AutoApprovalRuleDto>> ListRulesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<AutoApprovalRuleDto>());
        public Task<AutoApprovalRuleDto> CreateRuleAsync(CreateAutoApprovalRuleDto dto, string createdBy, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<bool> ToggleRuleAsync(Guid ruleId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task DeleteRuleAsync(Guid ruleId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    // ── License Stub ────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal ILicenseService stub for testing feature gating logic.
    /// Uses the same FeatureMinTier mapping as the real LicenseService.
    /// </summary>
    private sealed class StubLicenseService(LicenseTier tier) : ILicenseService
    {
        private static readonly Dictionary<LicenseFeature, LicenseTier> FeatureMinTier = new()
        {
            [LicenseFeature.AutoApprovalRules]  = LicenseTier.Business,
            [LicenseFeature.ExcelExport]        = LicenseTier.Business,
            [LicenseFeature.AgentAutoUpdate]    = LicenseTier.Business,
            [LicenseFeature.SyslogForwarding]   = LicenseTier.Business,
            [LicenseFeature.RegistrationTokens] = LicenseTier.Business,
            [LicenseFeature.MultiApprover]      = LicenseTier.Business,
            [LicenseFeature.RealtimeToasts]     = LicenseTier.Enterprise,
        };

        public LicenseInfo? License => null;
        public bool IsValid => true;
        public bool IsHardwareValid => true;
        public int ActiveEndpoints => 0;
        public int DormantEndpoints => 0;
        public int MaxEndpoints => 999;
        public int AvailableSeats => 999;
        public bool HasAvailableSeats => true;
        public bool IsInGracePeriod => false;
        public DateTimeOffset? GraceExpiresAt => null;
        public int DormantDays => 30;
        public string StatusSummary => "Test";
        public string LicenseFilePath => "test.lic";
        public LicenseTier Tier => tier;

        public bool HasFeature(LicenseFeature feature) => true;

        public Task InitializeAsync() => Task.CompletedTask;
        public Task RefreshSeatCountAsync() => Task.CompletedTask;
        public bool TryConsumeSeat() => true;
        public string GetHardwareFingerprint() => "test";
    }
}
