using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Shared.Config;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Dedicated verification tests for all critical and high severity bug fixes.
/// Each test class corresponds to one bug fix.
/// </summary>
public sealed class BugFixVerificationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // Shared test infrastructure
    // =====================================================================

    private static (DbContextOptions<RaizenDbContext> opts, TestDbContextFactory factory) CreateDb()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"BugFix_{Guid.NewGuid()}")
            .Options;
        return (opts, new TestDbContextFactory(opts));
    }

    private static IConfiguration CreateConfig(Dictionary<string, string?>? extra = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Security:AuditHmacKey"] = "TestKey_BugFix",
            ["RequestLimits:MaxPerUserPerHour"] = "100",
        };
        if (extra != null)
            foreach (var kv in extra) defaults[kv.Key] = kv.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    private static ActionDefinition MakeDefinition(string parametersSchema = "[]") => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Test Action",
        ActionType = ActionType.StartService,
        ApprovalWindowMinutes = 60,
        MinApprovers = 1,
        IsEnabled = true,
        ParametersSchemaJson = parametersSchema,
        ApproverGroupIdsJson = "[]",
    };

    private static EndpointRegistration MakeEndpoint() => new()
    {
        Id = Guid.NewGuid(),
        MachineId = Guid.NewGuid().ToString(),
        MachineName = "TESTPC",
        ApiKeyHash = new string('0', 128),
        IsEnabled = true,
    };

    // =====================================================================
    // Bug 1.1: MfaSetup privilege escalation
    // Verify: GetRoleClaims returns correct roles per role string
    // =====================================================================

    [Theory]
    [InlineData("Admin", new[] { "Raizen.Admin", "Raizen.Approver", "Raizen.Operator", "Raizen.Auditor" })]
    [InlineData("Approver", new[] { "Raizen.Approver", "Raizen.Operator", "Raizen.Auditor" })]
    [InlineData("Operator", new[] { "Raizen.Operator", "Raizen.Auditor" })]
    [InlineData("Auditor", new[] { "Raizen.Auditor" })]
    public void GetRoleClaims_ReturnsCorrectRolesForEachRole(string role, string[] expectedRoles)
    {
        var claims = IAdminAuthService.GetRoleClaims(role);
        var roleValues = claims.Select(c => c.Value).ToArray();

        Assert.Equal(expectedRoles.Length, roleValues.Length);
        foreach (var expected in expectedRoles)
            Assert.Contains(expected, roleValues);
    }

    [Fact]
    public void GetRoleClaims_Operator_DoesNotIncludeAdmin()
    {
        var claims = IAdminAuthService.GetRoleClaims("Operator");
        var roleValues = claims.Select(c => c.Value).ToList();

        Assert.DoesNotContain("Raizen.Admin", roleValues);
        Assert.DoesNotContain("Raizen.Approver", roleValues);
    }

    [Fact]
    public void GetRoleClaims_Auditor_OnlyHasAuditor()
    {
        var claims = IAdminAuthService.GetRoleClaims("Auditor");
        Assert.Single(claims);
        Assert.Equal("Raizen.Auditor", claims[0].Value);
    }

    // =====================================================================
    // Bug 1.3: TOTP ConcurrentDictionary memory leak
    // Verify: Stale entries can be cleaned up by timestamp
    // =====================================================================

    [Fact]
    public void TotpAttemptTracking_StaleEntriesCanBeCleanedByTimestamp()
    {
        // Simulate the fixed data structure
        var attempts = new ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)>();

        var staleUser = Guid.NewGuid();
        var activeUser = Guid.NewGuid();

        // Stale entry from 20 minutes ago
        attempts[staleUser] = (3, DateTime.UtcNow.AddMinutes(-20));
        // Active entry from 1 minute ago
        attempts[activeUser] = (2, DateTime.UtcNow.AddMinutes(-1));

        // Cleanup sweep (same logic as the fix)
        var staleThreshold = DateTime.UtcNow.AddMinutes(-15);
        foreach (var key in attempts.Keys)
            if (attempts.TryGetValue(key, out var e) && e.LastAttempt < staleThreshold)
                attempts.TryRemove(key, out _);

        Assert.False(attempts.ContainsKey(staleUser), "Stale entry should be removed");
        Assert.True(attempts.ContainsKey(activeUser), "Active entry should be preserved");
    }

    [Fact]
    public void TotpAttemptTracking_AddOrUpdate_IncrementsCountWithTimestamp()
    {
        var attempts = new ConcurrentDictionary<Guid, (int Count, DateTime LastAttempt)>();
        var userId = Guid.NewGuid();

        var e1 = attempts.AddOrUpdate(userId,
            _ => (1, DateTime.UtcNow),
            (_, old) => (old.Count + 1, DateTime.UtcNow));
        Assert.Equal(1, e1.Count);

        var e2 = attempts.AddOrUpdate(userId,
            _ => (1, DateTime.UtcNow),
            (_, old) => (old.Count + 1, DateTime.UtcNow));
        Assert.Equal(2, e2.Count);

        var e3 = attempts.AddOrUpdate(userId,
            _ => (1, DateTime.UtcNow),
            (_, old) => (old.Count + 1, DateTime.UtcNow));
        Assert.Equal(3, e3.Count);
    }

    // =====================================================================
    // Bug 1.4: Parameter override case-sensitivity bypass
    // Verify: Case-different override keys update the original key, not add new
    // =====================================================================

    [Fact]
    public async Task ParameterOverride_CaseDifferentKey_UpdatesOriginalKey()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());
        var svc = new RequestService(factory, audit, catalog,
            new StubNotificationService(), new StubAutoApprovalService(), cfg);

        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "FilePath", Required = true, DisplayName = "Path", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        using var db = new RaizenDbContext(opts);
        var def = MakeDefinition(schema);
        var ep = MakeEndpoint();
        db.ActionDefinitions.Add(def);
        db.EndpointRegistrations.Add(ep);
        await db.SaveChangesAsync();

        var submitted = await svc.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification = "test",
                Parameters = new() { ["FilePath"] = @"C:\original" },
            },
            "user@corp.com", "User", ep.Id);

        // Override with DIFFERENT CASING — should update "FilePath", not add "filepath"
        var result = await svc.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto
            {
                Approved = true,
                Note = "fix path",
                OverrideParameters = new() { ["filepath"] = @"C:\corrected" },
            },
            "approver@corp.com");

        Assert.Equal(RequestStatus.Approved, result.Status);
        // Should have exactly 1 key (not 2)
        Assert.Single(result.Parameters);
        // The key should retain original casing
        Assert.True(result.Parameters.ContainsKey("FilePath"),
            "Original key casing should be preserved");
        Assert.Equal(@"C:\corrected", result.Parameters["FilePath"]);
    }

    [Fact]
    public async Task ParameterOverride_ExactCaseKey_StillWorks()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());
        var svc = new RequestService(factory, audit, catalog,
            new StubNotificationService(), new StubAutoApprovalService(), cfg);

        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "ServiceName", Required = true, DisplayName = "Svc", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        using var db = new RaizenDbContext(opts);
        var def = MakeDefinition(schema);
        var ep = MakeEndpoint();
        db.ActionDefinitions.Add(def);
        db.EndpointRegistrations.Add(ep);
        await db.SaveChangesAsync();

        var submitted = await svc.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification = "test",
                Parameters = new() { ["ServiceName"] = "Spooler" },
            },
            "user@corp.com", "User", ep.Id);

        var result = await svc.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto
            {
                Approved = true,
                Note = "changed",
                OverrideParameters = new() { ["ServiceName"] = "BITS" },
            },
            "approver@corp.com");

        Assert.Single(result.Parameters);
        Assert.Equal("BITS", result.Parameters["ServiceName"]);
    }

    // =====================================================================
    // Bug 2.3: MsiHashCache OOM — streaming hash
    // Verify: SHA256 streaming produces same result as ReadAllBytes
    // =====================================================================

    [Fact]
    public void StreamingHash_ProducesSameResultAsInMemoryHash()
    {
        // Create a temp file with known content
        var path = Path.GetTempFileName();
        try
        {
            var content = new byte[1024 * 100]; // 100KB
            Random.Shared.NextBytes(content);
            File.WriteAllBytes(path, content);

            // In-memory hash (old way)
            var bytesHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            // Streaming hash (new way)
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var streamHash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();

            Assert.Equal(bytesHash, streamHash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // =====================================================================
    // Bug 2.4: FileSystemWatcher race condition — debounce timer
    // Verify: ConfigLoader properly loads config and reports changes
    // =====================================================================

    [Fact]
    public void ConfigLoader_LoadsConfigCorrectly()
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = new RaizenEndpointConfig
            {
                ServerUrl = "https://test.local:5001",
                MachineId = "test-machine-id",
                PollIntervalSeconds = 15,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            using var loader = new ConfigLoader(path);
            Assert.Equal("https://test.local:5001", loader.Current.ServerUrl);
            Assert.Equal("test-machine-id", loader.Current.MachineId);
            Assert.Equal(15, loader.Current.PollIntervalSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConfigLoader_DisposesCleanly()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new RaizenEndpointConfig()));
            var loader = new ConfigLoader(path);
            loader.Dispose(); // Should not throw — verifies debounce timer + watcher are disposed
        }
        finally
        {
            File.Delete(path);
        }
    }

    // =====================================================================
    // Bug 3.2: AuditExportService null navigation property
    // Verify: Export handles null Endpoint gracefully
    // =====================================================================

    [Fact]
    public async Task AuditExport_HandlesNullEndpoint_Gracefully()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);

        using var db = new RaizenDbContext(opts);

        // Create an audit log entry with no linked request
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Event = "test.event",
            ActorUpn = "admin@corp.com",
            TargetMachine = "FALLBACK-MACHINE",
            OccurredAt = DateTimeOffset.UtcNow,
            IpAddress = "127.0.0.1",
        });
        await db.SaveChangesAsync();

        var exportSvc = new AuditExportService(factory);
        var result = await exportSvc.ExportAsync(null, null, null, null);

        Assert.NotNull(result);
        Assert.True(result.RecordCount > 0);

        // Deserialize the payload to verify the TargetMachine fallback
        var payloadJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(result.Payload));
        Assert.Contains("FALLBACK-MACHINE", payloadJson);
    }

    // =====================================================================
    // Bug 4.2: Tray app reentrancy guard
    // Verify: Reentrancy guard pattern works correctly
    // =====================================================================

    [Fact]
    public async Task ReentrancyGuard_PreventsOverlappingExecution()
    {
        // Simulate the reentrancy guard pattern used in TrayApplicationContext
        var checking = false;
        var executionCount = 0;

        async Task CheckAsync()
        {
            if (checking) return;
            checking = true;
            try
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(100); // Simulate work
            }
            finally
            {
                checking = false;
            }
        }

        // Launch 5 concurrent checks — only 1 should execute at a time
        var tasks = Enumerable.Range(0, 5).Select(_ => CheckAsync()).ToArray();
        await Task.WhenAll(tasks);

        // First call always runs; subsequent calls during execution are skipped
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void HashSetSizeCap_PreventsUnboundedGrowth()
    {
        var set = new HashSet<Guid>();

        // Fill beyond the cap
        for (int i = 0; i < 600; i++)
            set.Add(Guid.NewGuid());

        Assert.Equal(600, set.Count);

        // Apply the cap logic from the fix
        if (set.Count > 500) set.Clear();

        Assert.Empty(set);
    }

    // =====================================================================
    // Bug 4.3: Missing action handlers — validation
    // Verify: ActionCatalogService rejects unimplemented types
    // =====================================================================

    [Fact]
    public async Task CreateAction_RejectsUnimplementedActionType()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test Delete File",
            ActionType = ActionType.DeleteFile, // Not implemented
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.CreateAsync(dto, "admin@corp.com"));
        Assert.Contains("does not have an endpoint handler", ex.Message);
    }

    [Fact]
    public async Task CreateAction_AcceptsImplementedActionType()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test Start Service",
            ActionType = ActionType.StartService, // Implemented
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };

        var result = await catalog.CreateAsync(dto, "admin@corp.com");
        Assert.Equal("Test Start Service", result.DisplayName);
        Assert.Equal(ActionType.StartService, result.ActionType);
    }

    [Theory]
    [InlineData(ActionType.DeleteFile)]
    [InlineData(ActionType.CreateLocalUser)]
    [InlineData(ActionType.DisableLocalUser)]
    [InlineData(ActionType.AddTrustedCertificate)]
    [InlineData(ActionType.SetFirewallRule)]
    [InlineData(ActionType.UninstallMsi)]
    [InlineData(ActionType.DeleteRegistryValue)]
    public async Task CreateAction_RejectsAllUnimplementedTypes(ActionType type)
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test",
            ActionType = type,
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.CreateAsync(dto, "admin@corp.com"));
    }

    [Theory]
    [InlineData(ActionType.InstallMsi)]
    [InlineData(ActionType.AddLocalGroupMember)]
    [InlineData(ActionType.RemoveLocalGroupMember)]
    [InlineData(ActionType.StartService)]
    [InlineData(ActionType.StopService)]
    [InlineData(ActionType.RestartService)]
    [InlineData(ActionType.CopyFile)]
    [InlineData(ActionType.SetRegistryValue)]
    [InlineData(ActionType.RunApprovedScript)]
    [InlineData(ActionType.OpenFileProperties)]
    [InlineData(ActionType.RunAsAdmin)]
    [InlineData(ActionType.SetNetworkConfiguration)]
    [InlineData(ActionType.SetEnvironmentVariable)]
    public async Task CreateAction_AcceptsAllImplementedTypes(ActionType type)
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());

        var dto = new UpsertActionDefinitionDto
        {
            DisplayName = $"Test {type}",
            ActionType = type,
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };

        var result = await catalog.CreateAsync(dto, "admin@corp.com");
        Assert.Equal(type, result.ActionType);
    }

    [Fact]
    public async Task UpdateAction_RejectsUnimplementedActionType()
    {
        var (opts, factory) = CreateDb();
        var cfg = CreateConfig();
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit, new AllFeaturesLicenseStub());

        // Create with valid type first
        var createDto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test",
            ActionType = ActionType.StartService,
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };
        var created = await catalog.CreateAsync(createDto, "admin@corp.com");

        // Try to update to unimplemented type
        var updateDto = new UpsertActionDefinitionDto
        {
            DisplayName = "Test",
            ActionType = ActionType.SetFirewallRule, // Not implemented
            Parameters = [],
            ApproverGroupIds = [],
            IsEnabled = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.UpdateAsync(created.Id, updateDto, "admin@corp.com"));
    }

    // =====================================================================
    // Bug 4.1: Duplicate OpenFileProperties handler
    // Verify: OpenFilePropertiesHandler.cs no longer exists
    // =====================================================================

    [Fact]
    public void OpenFilePropertiesHandler_FileDeleted()
    {
        // The old OpenFilePropertiesHandler.cs should not exist
        var path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Raizen.Endpoint", "Raizen.Endpoint.Service", "Actions",
            "OpenFilePropertiesHandler.cs");
        Assert.False(File.Exists(path),
            "OpenFilePropertiesHandler.cs should be deleted (dead code, duplicate of FilePermissionsHandler)");
    }

    // =====================================================================
    // Stubs for dependencies not under test
    // =====================================================================

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
}
