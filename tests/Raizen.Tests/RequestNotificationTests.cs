using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class RequestNotificationTests
{
    [Fact]
    public async Task ReportExecutionResult_SendsCompletedNotification()
    {
        await using var fixture = await RequestFixture.CreateAsync();

        var result = await fixture.Service.ReportExecutionResultAsync(new ExecutionResultDto
        {
            RequestId = fixture.RequestId,
            Succeeded = true,
            ResultMessage = "Completed test action.",
            ExecutedAt = DateTimeOffset.UtcNow,
        }, fixture.EndpointId);

        Assert.Equal(RequestStatus.Succeeded, result.Status);
        var sent = Assert.Single(fixture.Notifications.CompletedRequests);
        Assert.Equal(fixture.RequestId, sent.Id);
        Assert.Equal(RequestStatus.Succeeded, sent.Status);
    }

    [Fact]
    public async Task ReportExecutionResult_NotificationFailure_DoesNotBlockStatusUpdate()
    {
        await using var fixture = await RequestFixture.CreateAsync(throwOnCompleted: true);

        var result = await fixture.Service.ReportExecutionResultAsync(new ExecutionResultDto
        {
            RequestId = fixture.RequestId,
            Succeeded = false,
            ErrorMessage = "Endpoint reported failure.",
            ExecutedAt = DateTimeOffset.UtcNow,
        }, fixture.EndpointId);

        Assert.Equal(RequestStatus.Failed, result.Status);
        Assert.Equal(1, fixture.Notifications.CompletedAttempts);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.ElevationRequests.FindAsync(fixture.RequestId);
        Assert.NotNull(saved);
        Assert.Equal(RequestStatus.Failed, saved.Status);
    }

    private sealed class RequestFixture : IAsyncDisposable
    {
        private RequestFixture(
            RaizenDbContext db,
            RequestService service,
            RecordingNotificationService notifications,
            Guid requestId,
            Guid endpointId)
        {
            Db = db;
            Service = service;
            Notifications = notifications;
            RequestId = requestId;
            EndpointId = endpointId;
        }

        public RaizenDbContext Db { get; }
        public RequestService Service { get; }
        public RecordingNotificationService Notifications { get; }
        public Guid RequestId { get; }
        public Guid EndpointId { get; }

        public static async Task<RequestFixture> CreateAsync(bool throwOnCompleted = false)
        {
            var opts = new DbContextOptionsBuilder<RaizenDbContext>()
                .UseInMemoryDatabase($"RequestNotify_{Guid.NewGuid()}")
                .Options;
            var db = new RaizenDbContext(opts);

            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:AuditHmacKey"] = "TestKey_RequestNotify",
                })
                .Build();

            var factory = new TestDbContextFactory(opts);
            var audit = new AuditService(factory, new NullSyslogSender(), cfg);
            var catalog = new ActionCatalogService(factory, audit);
            var notifications = new RecordingNotificationService { ThrowOnCompleted = throwOnCompleted };
            var service = new RequestService(
                factory,
                audit,
                catalog,
                notifications,
                new AutoApprovalService(factory),
                cfg);

            var action = new ActionDefinition
            {
                Id = Guid.NewGuid(),
                DisplayName = "Restart Service",
                ActionType = ActionType.RestartService,
                IsEnabled = true,
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
            var request = new ElevationRequest
            {
                Id = Guid.NewGuid(),
                ActionDefinitionId = action.Id,
                EndpointRegistrationId = endpoint.Id,
                RequesterUpn = "requester@example.test",
                RequesterDisplayName = "Requester",
                Justification = "Testing completion notifications.",
                ParametersJson = "{}",
                OriginalParametersJson = "{}",
                Status = RequestStatus.Executing,
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(50),
                ReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ReviewerUpn = "approver@example.test",
            };

            db.ActionDefinitions.Add(action);
            db.EndpointRegistrations.Add(endpoint);
            db.ElevationRequests.Add(request);
            await db.SaveChangesAsync();

            return new RequestFixture(db, service, notifications, request.Id, endpoint.Id);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<ElevationRequestDto> CompletedRequests { get; } = [];
        public int CompletedAttempts { get; private set; }
        public bool ThrowOnCompleted { get; init; }

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

        public Task SendRequestCompletedAsync(ElevationRequestDto request, CancellationToken ct = default)
        {
            CompletedAttempts++;
            if (ThrowOnCompleted)
                throw new InvalidOperationException("Notification failure");

            CompletedRequests.Add(request);
            return Task.CompletedTask;
        }
    }
}
