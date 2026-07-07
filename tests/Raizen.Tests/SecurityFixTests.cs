using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Tests for the security fixes:
///   1. Parameter override merge (not replace)
///   2. Session timeout no longer hardcoded (verified at code level)
///   3. Poll signature verification fail-closed when key is missing
/// </summary>
public sealed class SecurityFixTests : IDisposable
{
    private readonly DbContextOptions<RaizenDbContext> _dbOpts;
    private readonly RaizenDbContext _db;
    private readonly RequestService _requestService;
    private readonly AuditService _audit;

    public SecurityFixTests()
    {
        _dbOpts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"SecurityFix_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(_dbOpts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_SecurityFix",
                ["RequestLimits:MaxPerUserPerHour"] = "100",
            })
            .Build();

        var syslog = new NullSyslogSender();
        _audit = new AuditService(new TestDbContextFactory(_dbOpts), syslog, cfg);
        var catalog = new ActionCatalogService(new TestDbContextFactory(_dbOpts), _audit);
        var notifications = new StubNotificationService();
        var autoApproval = new StubAutoApprovalService();

        _requestService = new RequestService(
            new TestDbContextFactory(_dbOpts),
            _audit,
            catalog,
            notifications,
            autoApproval,
            cfg);
    }

    public void Dispose() => _db.Dispose();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ActionDefinition MakeDefinition(
        int minApprovers = 1,
        string parametersSchema = "[]") => new()
    {
        Id                    = Guid.NewGuid(),
        DisplayName           = "Test Action",
        ActionType            = ActionType.StartService,
        ApprovalWindowMinutes = 60,
        MinApprovers          = minApprovers,
        IsEnabled             = true,
        ParametersSchemaJson  = parametersSchema,
        ApproverGroupIdsJson  = "[]",
    };

    private EndpointRegistration MakeEndpoint() => new()
    {
        Id          = Guid.NewGuid(),
        MachineId   = Guid.NewGuid().ToString(),
        MachineName = "TESTPC",
        ApiKeyHash  = new string('0', 128),
        IsEnabled   = true,
    };

    private async Task<(ActionDefinition def, EndpointRegistration ep)> SeedAsync(
        int minApprovers = 1,
        string parametersSchema = "[]")
    {
        var def = MakeDefinition(minApprovers, parametersSchema);
        var ep  = MakeEndpoint();
        _db.ActionDefinitions.Add(def);
        _db.EndpointRegistrations.Add(ep);
        await _db.SaveChangesAsync();
        return (def, ep);
    }

    // =====================================================================
    // Fix 1: Parameter override merge (not replace)
    // =====================================================================

    [Fact]
    public async Task OverrideParameters_MergesIntoOriginal_DoesNotReplace()
    {
        // Schema: two required params
        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "ServiceName",  Required = true, DisplayName = "Service",  ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
            new { Key = "MachineName",  Required = true, DisplayName = "Machine",  ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        var (def, ep) = await SeedAsync(parametersSchema: schema);

        // Submit with both params
        var submitted = await _requestService.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification      = "test",
                Parameters         = new() { ["ServiceName"] = "Spooler", ["MachineName"] = "SERVER01" },
            },
            "requester@corp.com", "Requester", ep.Id);

        // Approve with override on ServiceName only — MachineName must be preserved
        var result = await _requestService.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto
            {
                Approved            = true,
                Note                = "Changed service",
                OverrideParameters  = new() { ["ServiceName"] = "BITS" },
            },
            "approver@corp.com");

        Assert.Equal(RequestStatus.Approved, result.Status);
        Assert.Equal("BITS", result.Parameters["ServiceName"]);
        Assert.Equal("SERVER01", result.Parameters["MachineName"]);
    }

    [Fact]
    public async Task OverrideParameters_CanUpdateExistingValue()
    {
        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "FilePath", Required = true, DisplayName = "Path", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        var (def, ep) = await SeedAsync(parametersSchema: schema);

        var submitted = await _requestService.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification      = "test",
                Parameters         = new() { ["FilePath"] = @"C:\old\path.exe" },
            },
            "requester@corp.com", "Requester", ep.Id);

        var result = await _requestService.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto
            {
                Approved           = true,
                Note               = "Corrected path",
                OverrideParameters = new() { ["FilePath"] = @"C:\new\path.exe" },
            },
            "approver@corp.com");

        Assert.Equal(@"C:\new\path.exe", result.Parameters["FilePath"]);
    }

    [Fact]
    public async Task OverrideParameters_RejectsNewKeysNotInOriginal()
    {
        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "ServiceName", Required = true, DisplayName = "Service", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        var (def, ep) = await SeedAsync(parametersSchema: schema);

        var submitted = await _requestService.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification      = "test",
                Parameters         = new() { ["ServiceName"] = "Spooler" },
            },
            "requester@corp.com", "Requester", ep.Id);

        // Try to inject a new key "MaliciousParam" that wasn't in the original request
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _requestService.ReviewAsync(
                submitted.Id,
                new ReviewRequestDto
                {
                    Approved           = true,
                    Note               = "inject",
                    OverrideParameters = new() { ["MaliciousParam"] = "evil" },
                },
                "approver@corp.com"));

        Assert.Contains("not present in the original request", ex.Message);
    }

    [Fact]
    public async Task OverrideParameters_NoOverrides_OriginalParamsPreserved()
    {
        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "ServiceName", Required = true, DisplayName = "Service", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        var (def, ep) = await SeedAsync(parametersSchema: schema);

        var submitted = await _requestService.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification      = "test",
                Parameters         = new() { ["ServiceName"] = "Spooler" },
            },
            "requester@corp.com", "Requester", ep.Id);

        // Approve with no overrides
        var result = await _requestService.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto { Approved = true, Note = "LGTM" },
            "approver@corp.com");

        Assert.Equal("Spooler", result.Parameters["ServiceName"]);
    }

    [Fact]
    public async Task OverrideParameters_MultipleParams_OnlyOverriddenOneChanges()
    {
        var schema = JsonSerializer.Serialize(new[]
        {
            new { Key = "A", Required = true, DisplayName = "A", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
            new { Key = "B", Required = true, DisplayName = "B", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
            new { Key = "C", Required = true, DisplayName = "C", ParameterType = 0, ValidationPattern = "", DefaultValue = "" },
        }, JsonOpts);

        var (def, ep) = await SeedAsync(parametersSchema: schema);

        var submitted = await _requestService.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = def.Id,
                Justification      = "test",
                Parameters         = new() { ["A"] = "1", ["B"] = "2", ["C"] = "3" },
            },
            "requester@corp.com", "Requester", ep.Id);

        var result = await _requestService.ReviewAsync(
            submitted.Id,
            new ReviewRequestDto
            {
                Approved           = true,
                Note               = "fix B",
                OverrideParameters = new() { ["B"] = "CHANGED" },
            },
            "approver@corp.com");

        Assert.Equal("1", result.Parameters["A"]);
        Assert.Equal("CHANGED", result.Parameters["B"]);
        Assert.Equal("3", result.Parameters["C"]);
    }

    // =====================================================================
    // Fix 3: Poll signature verification fail-closed
    // =====================================================================

    private static (string privatePem, string publicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static PollResponseSigner CreateSigner(string privatePem)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:PollSigningPrivateKeyPem"] = privatePem,
            })
            .Build();
        return new PollResponseSigner(config, NullLogger<PollResponseSigner>.Instance);
    }

    [Fact]
    public void Verify_MissingPublicKey_ReturnsFalse_FailClosed()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign([]);

        // Empty key — must reject (fail-closed)
        Assert.False(PollSignatureVerifier.Verify(signed, "", NullLogger.Instance));
    }

    [Fact]
    public void Verify_NullPublicKey_ReturnsFalse_FailClosed()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign([]);

        // Null key — must reject (fail-closed)
        Assert.False(PollSignatureVerifier.Verify(signed, null, NullLogger.Instance));
    }

    [Fact]
    public void Verify_ValidKey_StillWorks()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign([new ElevationRequestDto
        {
            Id = Guid.NewGuid(),
            ActionDefinitionId = Guid.NewGuid(),
            ActionType = ActionType.StartService,
            RequesterUpn = "user@test.com",
            ActionDisplayName = "Test",
            Parameters = new(),
            Status = RequestStatus.Approved,
            SubmittedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }]);

        // Valid key — must accept
        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_TamperedPayload_StillRejected()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign([]);

        var bytes = Convert.FromBase64String(signed.Payload);
        bytes[0] ^= 0xFF;
        signed.Payload = Convert.ToBase64String(bytes);

        Assert.False(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
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
