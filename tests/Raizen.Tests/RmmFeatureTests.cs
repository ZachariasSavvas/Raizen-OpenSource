using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Endpoint.Service.Actions;
using Raizen.Endpoint.Service;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class RmmFeatureTests : IDisposable
{
    private readonly DbContextOptions<RaizenDbContext> _options;
    private readonly RaizenDbContext _db;
    private readonly TestDbContextFactory _factory;
    private readonly AuditService _audit;
    private readonly DiagnosticBundleService _diagnostics;
    private readonly MonitoringService _monitoring;

    public RmmFeatureTests()
    {
        _options = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"Rmm_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(_options);
        _factory = new TestDbContextFactory(_options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "RmmFeatureTests-Dedicated-Audit-Key",
            })
            .Build();
        _audit = new AuditService(_factory, new NullSyslogSender(), config);
        _diagnostics = new DiagnosticBundleService(_factory, _audit);
        _monitoring = new MonitoringService(_factory, _audit, new NullNotificationService());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void DiagnosticOptions_RejectUnapprovedChannel()
    {
        var parameters = new Dictionary<string, string> { ["Channels"] = "Security" };

        var ex = Assert.Throws<ArgumentException>(() => CollectEventLogsHandler.ParseOptions(parameters));

        Assert.Contains("only System", ex.Message);
    }

    [Fact]
    public void DiagnosticOptions_EnforceTimeAndEventBounds()
    {
        Assert.Throws<ArgumentException>(() => CollectEventLogsHandler.ParseOptions(
            new Dictionary<string, string> { ["Hours"] = "73" }));
        Assert.Throws<ArgumentException>(() => CollectEventLogsHandler.ParseOptions(
            new Dictionary<string, string> { ["MaxEvents"] = "1001" }));
    }

    [Fact]
    public void EndpointInventory_IsBoundedAndNotRecollectedEveryHeartbeat()
    {
        var collector = new EndpointHealthCollector();

        var first = collector.Collect();
        var second = collector.Collect();

        Assert.True(first.ProcessesCollected);
        Assert.True(first.ServicesCollected);
        Assert.InRange(first.Processes.Count, 1, 100);
        Assert.InRange(first.Services.Count, 1, 500);
        Assert.False(second.ProcessesCollected);
        Assert.False(second.ServicesCollected);
    }

    [Fact]
    public async Task DiagnosticUpload_PersistsVerifiedBundleForExecutingRequest()
    {
        var endpoint = NewEndpoint("DIAG-PC");
        var action = new ActionDefinition
        {
            DisplayName = "Collect diagnostics",
            Description = "Test",
            ActionType = ActionType.CollectEventLogs,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
            CreatedByUpn = "test",
        };
        var request = new ElevationRequest
        {
            Endpoint = endpoint,
            ActionDefinition = action,
            RequesterUpn = "admin@test",
            RequesterDisplayName = "Admin",
            Justification = "Test collection",
            Status = RequestStatus.Executing,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        _db.Add(request);
        await _db.SaveChangesAsync();
        var content = "PK-test-zip-content"u8.ToArray();

        var result = await _diagnostics.UploadAsync(endpoint.Id, new DiagnosticBundleUploadDto
        {
            RequestId = request.Id,
            FileName = "Raizen-Diagnostics-DIAG-PC.zip",
            ContentBase64 = Convert.ToBase64String(content),
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            EventCount = 12,
            GeneratedAt = DateTimeOffset.UtcNow,
        });

        Assert.NotEqual(Guid.Empty, result.Id);
        var saved = await _diagnostics.DownloadAsync(result.Id);
        Assert.NotNull(saved);
        Assert.Equal(content, saved!.Value.Content);
        Assert.Single(await _diagnostics.ListAsync(endpoint.Id));
    }

    [Fact]
    public async Task Monitoring_LowDiskCreatesAlertAndHealthyDiskResolvesIt()
    {
        var endpoint = NewEndpoint("LOW-DISK-PC");
        endpoint.LastSeenAt = DateTimeOffset.UtcNow;
        endpoint.HealthReportedAt = DateTimeOffset.UtcNow;
        endpoint.SystemDriveFreePercent = 5;
        _db.Add(endpoint);
        await _db.SaveChangesAsync();

        await _monitoring.EvaluateEndpointAsync(endpoint.Id);

        var active = await _monitoring.ListAlertsAsync();
        Assert.Contains(active, x => x.EndpointRegistrationId == endpoint.Id
            && x.RuleType == MonitoringRuleType.LowSystemDriveFreePercent);

        endpoint.SystemDriveFreePercent = 50;
        endpoint.HealthReportedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        await _monitoring.EvaluateEndpointAsync(endpoint.Id);

        Assert.DoesNotContain(await _monitoring.ListAlertsAsync(), x =>
            x.EndpointRegistrationId == endpoint.Id
            && x.RuleType == MonitoringRuleType.LowSystemDriveFreePercent);
    }

    [Fact]
    public void Monitoring_EvaluationDetectsDefenderDisabled()
    {
        var endpoint = NewEndpoint("DEFENDER-PC");
        endpoint.HealthReportedAt = DateTimeOffset.UtcNow;
        endpoint.DefenderEnabled = false;
        var rule = MonitoringService.DefaultRules()
            .Single(x => x.RuleType == MonitoringRuleType.DefenderDisabled);

        var result = MonitoringService.Evaluate(endpoint, rule, DateTimeOffset.UtcNow);

        Assert.True(result.Violated);
    }

    [Fact]
    public void Monitoring_EvaluationDetectsStoppedService()
    {
        var endpoint = NewEndpoint("SERVICE-PC");
        endpoint.ServiceInventoryReportedAt = DateTimeOffset.UtcNow;
        endpoint.ServicesJson = """[{"name":"Spooler","displayName":"Print Spooler","status":"Stopped","startMode":"Auto"}]""";
        var rule = new MonitoringRule
        {
            RuleType = MonitoringRuleType.ServiceStopped,
            TargetServiceName = "Spooler",
        };

        var result = MonitoringService.Evaluate(endpoint, rule, DateTimeOffset.UtcNow);

        Assert.True(result.HasData);
        Assert.True(result.Violated);
        Assert.Contains("Stopped", result.Message);
    }

    [Fact]
    public async Task Monitoring_ServiceRuleSendsEmailOnlyWhenAlertFirstTriggers()
    {
        var endpoint = NewEndpoint("EMAIL-SERVICE-PC");
        endpoint.LastSeenAt = DateTimeOffset.UtcNow;
        endpoint.ServiceInventoryReportedAt = DateTimeOffset.UtcNow;
        endpoint.ServicesJson = """[{"name":"W32Time","displayName":"Windows Time","status":"Stopped","startMode":"Auto"}]""";
        _db.Add(endpoint);
        await _db.SaveChangesAsync();
        var recording = new RecordingNotificationService();
        var monitoring = new MonitoringService(_factory, _audit, recording);
        await monitoring.CreateServiceRuleAsync(new ServiceMonitoringRuleUpsertDto
        {
            EndpointRegistrationId = endpoint.Id,
            ServiceName = "W32Time",
            NotifyByEmail = true,
            NotificationRecipients = ["owner@example.com"],
        }, "admin@test");

        await monitoring.EvaluateEndpointAsync(endpoint.Id);
        await monitoring.EvaluateEndpointAsync(endpoint.Id);

        Assert.Single(recording.Alerts);
        Assert.Equal("owner@example.com", recording.Alerts[0].Recipients.Single());
        Assert.Contains(await monitoring.ListAlertsAsync(), x =>
            x.RuleType == MonitoringRuleType.ServiceStopped && x.EndpointRegistrationId == endpoint.Id);
    }

    [Fact]
    public async Task Monitoring_DeleteBuiltInRuleHidesItAndDoesNotRecreateIt()
    {
        var endpoint = NewEndpoint("DELETE-RULE-PC");
        endpoint.LastSeenAt = DateTimeOffset.UtcNow;
        endpoint.HealthReportedAt = DateTimeOffset.UtcNow;
        endpoint.PendingReboot = true;
        _db.Add(endpoint);
        await _db.SaveChangesAsync();
        var rule = (await _monitoring.ListRulesAsync())
            .Single(x => x.RuleType == MonitoringRuleType.PendingReboot);
        await _monitoring.EvaluateEndpointAsync(endpoint.Id);
        Assert.Contains(await _monitoring.ListAlertsAsync(), x => x.MonitoringRuleId == rule.Id);

        await _monitoring.DeleteRuleAsync(rule.Id, "admin@test");
        await _monitoring.EnsureDefaultRulesAsync();

        Assert.DoesNotContain(await _monitoring.ListRulesAsync(), x => x.Id == rule.Id);
        Assert.DoesNotContain(await _monitoring.ListAlertsAsync(), x => x.MonitoringRuleId == rule.Id);
        var stored = await _db.MonitoringRules.FindAsync(rule.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.IsDeleted);
    }

    private static EndpointRegistration NewEndpoint(string name) => new()
    {
        MachineId = Guid.NewGuid().ToString("N"),
        MachineName = name,
        ApiKeyHash = new string('0', 128),
        IsEnabled = true,
    };

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(string Machine, IReadOnlyCollection<string> Recipients)> Alerts { get; } = [];
        public Task<NotificationSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(new NotificationSettings());
        public Task SaveAsync(NotificationSettings settings, string actorUpn, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> TestAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SendRequestSubmittedAsync(ElevationRequestDto request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRequestReviewedAsync(ElevationRequestDto request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRequestCompletedAsync(ElevationRequestDto request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMonitoringAlertAsync(string machineName, string ruleName, MonitoringSeverity severity,
            string detail, IReadOnlyCollection<string> recipients, CancellationToken ct = default)
        {
            Alerts.Add((machineName, recipients));
            return Task.CompletedTask;
        }
    }
}
