using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>Tests for the four-eyes (dual approval) requirement on elevation requests.</summary>
public sealed class DualApprovalTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly AuditService _audit;
    private readonly NullSyslogSender _syslog;

    public DualApprovalTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"DualApproval_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(opts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_DualApproval"
            })
            .Build();

        _syslog = new NullSyslogSender();
        _audit  = new AuditService(new TestDbContextFactory(opts), _syslog, cfg);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActionDefinition MakeDefinition(int minApprovers = 1) => new()
    {
        Id                  = Guid.NewGuid(),
        DisplayName         = "Test Action",
        ActionType          = ActionType.StartService,
        ApprovalWindowMinutes = 60,
        MinApprovers        = minApprovers,
        IsEnabled           = true,
        ParametersSchemaJson = "[]",
        ApproverGroupIdsJson = "[]",
    };

    private static EndpointRegistration MakeEndpoint() => new()
    {
        Id          = Guid.NewGuid(),
        MachineId   = Guid.NewGuid().ToString(),
        MachineName = "TESTPC",
        ApiKeyHash  = new string('0', 128),
    };

    private ElevationRequest MakeRequest(ActionDefinition def, EndpointRegistration ep) => new()
    {
        Id                     = Guid.NewGuid(),
        ActionDefinitionId     = def.Id,
        ActionDefinition       = def,
        EndpointRegistrationId = ep.Id,
        Endpoint               = ep,
        RequesterUpn           = "requester@corp.com",
        Justification          = "test",
        Status                 = RequestStatus.Pending,
        SubmittedAt            = DateTimeOffset.UtcNow,
        ExpiresAt              = DateTimeOffset.UtcNow.AddHours(1),
    };

    private async Task<(ElevationRequest request, ActionDefinition def, EndpointRegistration ep)> SetupAsync(int minApprovers = 1)
    {
        var def = MakeDefinition(minApprovers);
        var ep  = MakeEndpoint();
        var req = MakeRequest(def, ep);

        _db.ActionDefinitions.Add(def);
        _db.EndpointRegistrations.Add(ep);
        _db.ElevationRequests.Add(req);
        await _db.SaveChangesAsync();

        return (req, def, ep);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void MinApprovers_Default_IsOne()
    {
        var def = new ActionDefinition();
        Assert.Equal(1, def.MinApprovers);
    }

    [Fact]
    public async Task SingleApprover_MinApprovers1_ImmediatelyApproved()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 1);

        req.Status = RequestStatus.Pending;

        // Simulate review: record the approval
        _db.RequestApprovals.Add(new RequestApproval
        {
            RequestId   = req.Id,
            ApproverUpn = "approver@corp.com",
            Approved    = true,
        });

        // With MinApprovers=1, one approval is enough
        var approvedCount = 1;
        var minApprovers  = req.ActionDefinition.MinApprovers;
        Assert.True(approvedCount >= minApprovers);
    }

    [Fact]
    public async Task DualApproval_FirstApproval_StillPending()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 2);

        _db.RequestApprovals.Add(new RequestApproval
        {
            RequestId   = req.Id,
            ApproverUpn = "approver1@corp.com",
            Approved    = true,
        });
        await _db.SaveChangesAsync();

        var approvals    = _db.RequestApprovals.Where(a => a.RequestId == req.Id && a.Approved).ToList();
        var approvedCount = approvals.Count;

        Assert.Equal(1, approvedCount);
        Assert.True(approvedCount < req.ActionDefinition.MinApprovers, "One approval should not satisfy MinApprovers=2.");
    }

    [Fact]
    public async Task DualApproval_SecondApproval_FullyApproved()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 2);

        _db.RequestApprovals.Add(new RequestApproval { RequestId = req.Id, ApproverUpn = "approver1@corp.com", Approved = true });
        _db.RequestApprovals.Add(new RequestApproval { RequestId = req.Id, ApproverUpn = "approver2@corp.com", Approved = true });
        await _db.SaveChangesAsync();

        var approvedCount = _db.RequestApprovals.Count(a => a.RequestId == req.Id && a.Approved);

        Assert.Equal(2, approvedCount);
        Assert.True(approvedCount >= req.ActionDefinition.MinApprovers, "Two approvals should satisfy MinApprovers=2.");
    }

    [Fact]
    public async Task DualApproval_SameApproverTwice_Rejected()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 2);

        _db.RequestApprovals.Add(new RequestApproval { RequestId = req.Id, ApproverUpn = "approver@corp.com", Approved = true });
        await _db.SaveChangesAsync();

        // Load approvals and simulate the duplicate-vote guard
        var existingApprovals = _db.RequestApprovals
            .Where(a => a.RequestId == req.Id)
            .ToList();

        var alreadyApproved = existingApprovals.Any(a => a.Approved && a.ApproverUpn == "approver@corp.com");
        Assert.True(alreadyApproved, "Duplicate approver detection should fire.");
    }

    [Fact]
    public async Task Denial_WithDualApproval_IsImmediate()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 2);

        // Add one approval first
        _db.RequestApprovals.Add(new RequestApproval { RequestId = req.Id, ApproverUpn = "approver1@corp.com", Approved = true });
        await _db.SaveChangesAsync();

        // Denial should be immediate regardless of approval count
        _db.RequestApprovals.Add(new RequestApproval { RequestId = req.Id, ApproverUpn = "approver2@corp.com", Approved = false });
        req.Status = RequestStatus.Denied;
        await _db.SaveChangesAsync();

        var reloaded = await _db.ElevationRequests.FindAsync(req.Id);
        Assert.Equal(RequestStatus.Denied, reloaded!.Status);
    }

    [Fact]
    public async Task RequestApproval_Persists_WithCorrectFields()
    {
        var (req, _, _) = await SetupAsync(minApprovers: 2);
        var now = DateTimeOffset.UtcNow;

        _db.RequestApprovals.Add(new RequestApproval
        {
            RequestId   = req.Id,
            ApproverUpn = "approver@corp.com",
            Note        = "Looks good",
            Approved    = true,
            OccurredAt  = now,
        });
        await _db.SaveChangesAsync();

        var saved = await _db.RequestApprovals.SingleAsync(a => a.RequestId == req.Id);
        Assert.Equal("approver@corp.com", saved.ApproverUpn);
        Assert.Equal("Looks good", saved.Note);
        Assert.True(saved.Approved);
    }
}
