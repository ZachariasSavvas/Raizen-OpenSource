using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;
using Raizen.Shared.Security;

namespace Raizen.Tests;

public sealed class ActionRiskPolicyTests
{
    [Fact]
    public void RunAsAdmin_RequiresHumanReview()
    {
        Assert.True(ActionRiskPolicy.RequiresHumanReview(ActionType.RunAsAdmin));
        Assert.Equal("High risk", ActionRiskPolicy.Label(ActionType.RunAsAdmin));
    }

    [Fact]
    public void StartService_IsStandardRisk()
    {
        Assert.False(ActionRiskPolicy.RequiresHumanReview(ActionType.StartService));
        Assert.Equal("Standard", ActionRiskPolicy.Label(ActionType.StartService));
    }

    [Fact]
    public async Task HighRiskAction_WithAutoApproveFlag_RemainsPending()
    {
        await using var fixture = await RequestFixture.CreateAsync(ActionType.RunAsAdmin, autoApprove: true);

        var result = await fixture.Service.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = fixture.ActionId,
                Justification = "Run an approved elevated tool.",
            },
            "requester@example.test",
            "Requester",
            fixture.EndpointId);

        Assert.Equal(RequestStatus.Pending, result.Status);
        Assert.Null(result.ReviewerUpn);
    }

    [Fact]
    public async Task StandardAction_WithAutoApproveFlag_IsApproved()
    {
        await using var fixture = await RequestFixture.CreateAsync(ActionType.StartService, autoApprove: true);

        var result = await fixture.Service.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = fixture.ActionId,
                Justification = "Restart approved service.",
            },
            "requester@example.test",
            "Requester",
            fixture.EndpointId);

        Assert.Equal(RequestStatus.Approved, result.Status);
        Assert.Equal("system:auto-approve", result.ReviewerUpn);
    }

    [Fact]
    public async Task CatchAllAutoApprovalRule_DoesNotApproveHighRiskAction()
    {
        await using var fixture = await RequestFixture.CreateAsync(ActionType.RunAsAdmin, autoApprove: false);
        fixture.Db.AutoApprovalRules.Add(new AutoApprovalRule
        {
            Name = "Catch all",
            CreatedBy = "admin@example.test",
            IsEnabled = true,
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.SubmitAsync(
            new SubmitElevationRequestDto
            {
                ActionDefinitionId = fixture.ActionId,
                Justification = "Rule should not bypass manual approval.",
            },
            "requester@example.test",
            "Requester",
            fixture.EndpointId);

        Assert.Equal(RequestStatus.Pending, result.Status);
        Assert.Null(result.ReviewerUpn);
    }

    private sealed class RequestFixture : IAsyncDisposable
    {
        private RequestFixture(
            RaizenDbContext db,
            RequestService service,
            Guid actionId,
            Guid endpointId)
        {
            Db = db;
            Service = service;
            ActionId = actionId;
            EndpointId = endpointId;
        }

        public RaizenDbContext Db { get; }
        public RequestService Service { get; }
        public Guid ActionId { get; }
        public Guid EndpointId { get; }

        public static async Task<RequestFixture> CreateAsync(ActionType actionType, bool autoApprove)
        {
            var opts = new DbContextOptionsBuilder<RaizenDbContext>()
                .UseInMemoryDatabase($"ActionRisk_{Guid.NewGuid()}")
                .Options;
            var db = new RaizenDbContext(opts);

            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:AuditHmacKey"] = "TestKey_ActionRisk",
                    ["RequestLimits:MaxPerUserPerHour"] = "20",
                })
                .Build();

            var factory = new TestDbContextFactory(opts);
            var audit = new AuditService(factory, new NullSyslogSender(), cfg);
            var catalog = new ActionCatalogService(factory, audit);
            var service = new RequestService(
                factory,
                audit,
                catalog,
                new NullNotificationService(),
                new AutoApprovalService(factory),
                cfg);

            var action = new ActionDefinition
            {
                Id = Guid.NewGuid(),
                DisplayName = actionType.ToString(),
                ActionType = actionType,
                IsEnabled = true,
                AutoApprove = autoApprove,
                ApprovalWindowMinutes = 60,
                ParametersSchemaJson = "[]",
                ApproverGroupIdsJson = "[]",
            };
            var endpoint = new EndpointRegistration
            {
                Id = Guid.NewGuid(),
                MachineId = Guid.NewGuid().ToString(),
                MachineName = "TEST-ENDPOINT",
                ApiKeyHash = new string('0', 128),
                IsEnabled = true,
            };

            db.ActionDefinitions.Add(action);
            db.EndpointRegistrations.Add(endpoint);
            await db.SaveChangesAsync();

            return new RequestFixture(db, service, action.Id, endpoint.Id);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class NullNotificationService : INotificationService
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

        public Task SendRequestCompletedAsync(ElevationRequestDto request, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
