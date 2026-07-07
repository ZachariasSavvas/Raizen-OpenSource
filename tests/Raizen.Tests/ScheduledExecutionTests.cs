using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>Tests for the scheduled execution feature.</summary>
public sealed class ScheduledExecutionTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly RequestService _requestSvc;
    private readonly ActionDefinition _def;
    private readonly EndpointRegistration _ep;

    public ScheduledExecutionTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"Scheduled_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(opts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Scheduled",
            })
            .Build();

        var factory = new TestDbContextFactory(opts);
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit);
        var notifications = new NullNotificationService();
        var autoApproval = new AutoApprovalService(factory);

        _requestSvc = new RequestService(factory, audit, catalog, notifications, autoApproval, cfg);

        // Seed
        _def = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Action",
            ActionType = ActionType.StartService,
            IsEnabled = true,
            AutoApprove = true,
            ApprovalWindowMinutes = 60,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        _ep = new EndpointRegistration
        {
            Id = Guid.NewGuid(),
            MachineId = Guid.NewGuid().ToString(),
            MachineName = "TESTPC",
            ApiKeyHash = new string('0', 128),
            IsEnabled = true,
        };
        _db.ActionDefinitions.Add(_def);
        _db.EndpointRegistrations.Add(_ep);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Submit_WithSchedule_SetsScheduledForUtc()
    {
        var future = DateTimeOffset.UtcNow.AddHours(2);
        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "scheduled test",
            ScheduledForUtc = future,
        };

        var result = await _requestSvc.SubmitAsync(dto, "user@test.com", "User", _ep.Id);

        Assert.NotNull(result.ScheduledForUtc);
        Assert.Equal(future.Hour, result.ScheduledForUtc!.Value.Hour);
    }

    [Fact]
    public async Task Submit_WithPastSchedule_ThrowsError()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);
        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "past schedule",
            ScheduledForUtc = past,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _requestSvc.SubmitAsync(dto, "user@test.com", "User", _ep.Id));
    }

    [Fact]
    public async Task Submit_WithoutSchedule_LeavesNull()
    {
        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "immediate",
        };

        var result = await _requestSvc.SubmitAsync(dto, "user@test.com", "User", _ep.Id);
        Assert.Null(result.ScheduledForUtc);
    }

    [Fact]
    public async Task GetPendingExecution_ExcludesFutureScheduled()
    {
        // Submit an auto-approved request scheduled for 2 hours from now
        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "future scheduled",
            ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(2),
        };
        await _requestSvc.SubmitAsync(dto, "user@test.com", "User", _ep.Id);

        // Should NOT appear in pending execution list
        var pending = await _requestSvc.GetPendingExecutionAsync(_ep.Id);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task GetPendingExecution_IncludesNonScheduled()
    {
        // Submit an auto-approved request with no schedule
        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "immediate",
        };
        await _requestSvc.SubmitAsync(dto, "user@test.com", "User", _ep.Id);

        var pending = await _requestSvc.GetPendingExecutionAsync(_ep.Id);
        Assert.Single(pending);
    }

    [Fact]
    public async Task GetPendingExecution_IncludesPastScheduled()
    {
        // Create an approved request with a schedule in the past (already due)
        var request = new ElevationRequest
        {
            ActionDefinitionId = _def.Id,
            EndpointRegistrationId = _ep.Id,
            RequesterUpn = "user@test.com",
            Justification = "past schedule",
            Status = RequestStatus.Approved,
            SubmittedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ScheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(-5), // due 5 min ago
        };
        _db.ElevationRequests.Add(request);
        await _db.SaveChangesAsync();

        var pending = await _requestSvc.GetPendingExecutionAsync(_ep.Id);
        Assert.Contains(pending, p => p.Id == request.Id);
    }
}

/// <summary>Stub notification service for tests.</summary>
internal sealed class NullNotificationService : INotificationService
{
    public Task<NotificationSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(new NotificationSettings());
    public Task SaveAsync(NotificationSettings settings, string actorUpn, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> TestAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task SendRequestSubmittedAsync(ElevationRequestDto request, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendRequestReviewedAsync(ElevationRequestDto request, CancellationToken ct = default) => Task.CompletedTask;
}
