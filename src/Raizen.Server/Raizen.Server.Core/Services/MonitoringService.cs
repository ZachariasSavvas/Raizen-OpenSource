using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Text.Json;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public interface IMonitoringService
{
    Task EnsureDefaultRulesAsync(CancellationToken ct = default);
    Task<List<MonitoringRuleDto>> ListRulesAsync(CancellationToken ct = default);
    Task UpdateRuleAsync(Guid id, bool enabled, double threshold, MonitoringSeverity severity,
        bool notifyByEmail, IReadOnlyCollection<string> recipients, string actorUpn, CancellationToken ct = default);
    Task<MonitoringRuleDto> CreateServiceRuleAsync(ServiceMonitoringRuleUpsertDto dto, string actorUpn, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, string actorUpn, CancellationToken ct = default);
    Task<List<MonitoringAlertDto>> ListAlertsAsync(bool activeOnly = true, CancellationToken ct = default);
    Task AcknowledgeAsync(Guid id, string actorUpn, CancellationToken ct = default);
    Task EvaluateEndpointAsync(Guid endpointId, CancellationToken ct = default);
    Task EvaluateAllAsync(CancellationToken ct = default);
}

public sealed class MonitoringService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    IAuditService audit,
    INotificationService notifications) : IMonitoringService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    public async Task EnsureDefaultRulesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.MonitoringRules.Select(x => x.RuleType).ToListAsync(ct);
        var defaults = DefaultRules().Where(x => !existing.Contains(x.RuleType)).ToList();
        if (defaults.Count == 0) return;
        db.MonitoringRules.AddRange(defaults);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<MonitoringRuleDto>> ListRulesAsync(CancellationToken ct = default)
    {
        await EnsureDefaultRulesAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rules = await db.MonitoringRules.AsNoTracking()
            .Include(x => x.Endpoint)
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Severity).ThenBy(x => x.Name)
            .ToListAsync(ct);
        return rules.Select(MapRule).ToList();
    }

    public async Task UpdateRuleAsync(
        Guid id,
        bool enabled,
        double threshold,
        MonitoringSeverity severity,
        bool notifyByEmail,
        IReadOnlyCollection<string> recipients,
        string actorUpn,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(severity)) throw new ArgumentException("Invalid severity.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.MonitoringRules.FindAsync([id], ct)
            ?? throw new InvalidOperationException("Monitoring rule not found.");
        ValidateThreshold(rule.RuleType, threshold);
        rule.IsEnabled = enabled;
        rule.Threshold = threshold;
        rule.Severity = severity;
        rule.NotifyByEmail = notifyByEmail;
        rule.NotificationRecipientsJson = JsonSerializer.Serialize(NormalizeRecipients(recipients), JsonOpts);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("monitoring.rule-updated", actorUpn,
            targetMachine: rule.EndpointRegistrationId?.ToString(),
            detail: $"rule={rule.Name}; enabled={enabled}; severity={severity}; email={notifyByEmail}", ct: ct);
    }

    public async Task<MonitoringRuleDto> CreateServiceRuleAsync(
        ServiceMonitoringRuleUpsertDto dto,
        string actorUpn,
        CancellationToken ct = default)
    {
        var serviceName = NormalizeServiceName(dto.ServiceName);
        if (!Enum.IsDefined(dto.Severity)) throw new ArgumentException("Invalid severity.");
        var recipients = NormalizeRecipients(dto.NotificationRecipients);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        EndpointRegistration? endpoint = null;
        if (dto.EndpointRegistrationId.HasValue)
        {
            endpoint = await db.EndpointRegistrations.FindAsync([dto.EndpointRegistrationId.Value], ct)
                ?? throw new InvalidOperationException("Endpoint not found.");
        }

        var duplicate = await db.MonitoringRules.AnyAsync(x =>
            !x.IsDeleted
            &&
            x.RuleType == MonitoringRuleType.ServiceStopped
            && x.EndpointRegistrationId == dto.EndpointRegistrationId
            && x.TargetServiceName != null
            && x.TargetServiceName.ToLower() == serviceName.ToLower(), ct);
        if (duplicate) throw new InvalidOperationException("A monitoring rule already exists for this service and endpoint scope.");

        var rule = new MonitoringRule
        {
            RuleType = MonitoringRuleType.ServiceStopped,
            Name = $"Service stopped: {serviceName}",
            Description = endpoint is null
                ? $"Alert when the {serviceName} Windows service is not running on any endpoint."
                : $"Alert when the {serviceName} Windows service is not running on {endpoint.MachineName}.",
            Threshold = 0,
            Severity = dto.Severity,
            IsEnabled = dto.IsEnabled,
            EndpointRegistrationId = dto.EndpointRegistrationId,
            TargetServiceName = serviceName,
            NotifyByEmail = dto.NotifyByEmail,
            NotificationRecipientsJson = JsonSerializer.Serialize(recipients, JsonOpts),
        };
        db.MonitoringRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("monitoring.service-rule-created", actorUpn,
            targetMachine: endpoint?.MachineName,
            detail: $"service={serviceName}; scope={(endpoint?.MachineName ?? "all endpoints")}; email={dto.NotifyByEmail}", ct: ct);

        rule.Endpoint = endpoint;
        return MapRule(rule);
    }

    public async Task DeleteRuleAsync(Guid id, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.MonitoringRules.Include(x => x.Endpoint).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("Monitoring rule not found.");
        if (rule.IsDeleted) return;

        rule.IsDeleted = true;
        rule.IsEnabled = false;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;
        var activeAlerts = await db.MonitoringAlerts
            .Where(x => x.MonitoringRuleId == id && x.IsActive)
            .ToListAsync(ct);
        foreach (var alert in activeAlerts)
        {
            alert.IsActive = false;
            alert.ResolvedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("monitoring.rule-deleted", actorUpn,
            targetMachine: rule.Endpoint?.MachineName,
            detail: $"rule={rule.Name}; type={rule.RuleType}; resolvedAlerts={activeAlerts.Count}", ct: ct);
    }

    public async Task<List<MonitoringAlertDto>> ListAlertsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.MonitoringAlerts.AsNoTracking().AsQueryable();
        if (activeOnly) query = query.Where(x => x.IsActive);
        return await query
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.Rule.Severity)
            .ThenByDescending(x => x.LastObservedAt)
            .Take(500)
            .Select(x => new MonitoringAlertDto
            {
                Id = x.Id,
                EndpointRegistrationId = x.EndpointRegistrationId,
                MonitoringRuleId = x.MonitoringRuleId,
                MachineName = x.Endpoint.MachineName,
                RuleType = x.Rule.RuleType,
                RuleName = x.Rule.Name,
                Severity = x.Rule.Severity,
                Message = x.Message,
                ObservedValue = x.ObservedValue,
                IsActive = x.IsActive,
                TriggeredAt = x.TriggeredAt,
                LastObservedAt = x.LastObservedAt,
                ResolvedAt = x.ResolvedAt,
                AcknowledgedAt = x.AcknowledgedAt,
                AcknowledgedBy = x.AcknowledgedBy,
            })
            .ToListAsync(ct);
    }

    public async Task AcknowledgeAsync(Guid id, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var alert = await db.MonitoringAlerts.Include(x => x.Endpoint).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("Monitoring alert not found.");
        alert.AcknowledgedAt = DateTimeOffset.UtcNow;
        alert.AcknowledgedBy = actorUpn;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("monitoring.alert-acknowledged", actorUpn,
            targetMachine: alert.Endpoint.MachineName, detail: alert.Message, ct: ct);
    }

    public async Task EvaluateEndpointAsync(Guid endpointId, CancellationToken ct = default)
        => await EvaluateAsync(endpointId, ct);

    public async Task EvaluateAllAsync(CancellationToken ct = default)
        => await EvaluateAsync(null, ct);

    private async Task EvaluateAsync(Guid? endpointId, CancellationToken ct)
    {
        await EnsureDefaultRulesAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var endpointsQuery = db.EndpointRegistrations.Where(x => x.IsEnabled);
        if (endpointId.HasValue) endpointsQuery = endpointsQuery.Where(x => x.Id == endpointId.Value);
        var endpoints = await endpointsQuery.ToListAsync(ct);
        var rules = await db.MonitoringRules.Where(x => x.IsEnabled && !x.IsDeleted).ToListAsync(ct);
        var endpointIds = endpoints.Select(x => x.Id).ToList();
        var activeAlerts = await db.MonitoringAlerts
            .Where(x => x.IsActive && endpointIds.Contains(x.EndpointRegistrationId))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var auditEvents = new List<(string Event, string Machine, string Detail)>();
        var emailEvents = new List<(string Machine, string Rule, MonitoringSeverity Severity, string Detail, List<string> Recipients)>();

        foreach (var endpoint in endpoints)
        foreach (var rule in rules)
        {
            if (rule.EndpointRegistrationId.HasValue && rule.EndpointRegistrationId.Value != endpoint.Id)
                continue;

            var result = Evaluate(endpoint, rule, now);
            if (!result.HasData) continue;
            var existing = activeAlerts.FirstOrDefault(x =>
                x.EndpointRegistrationId == endpoint.Id && x.MonitoringRuleId == rule.Id);

            if (result.Violated)
            {
                if (existing is null)
                {
                    existing = new MonitoringAlert
                    {
                        EndpointRegistrationId = endpoint.Id,
                        MonitoringRuleId = rule.Id,
                        Message = result.Message,
                        ObservedValue = result.ObservedValue,
                        TriggeredAt = now,
                        LastObservedAt = now,
                    };
                    db.MonitoringAlerts.Add(existing);
                    activeAlerts.Add(existing);
                    auditEvents.Add(("monitoring.alert-triggered", endpoint.MachineName, $"{rule.Name}: {result.Message}"));
                    if (rule.NotifyByEmail)
                        emailEvents.Add((endpoint.MachineName, rule.Name, rule.Severity, result.Message,
                            DeserializeRecipients(rule.NotificationRecipientsJson)));
                }
                else
                {
                    existing.Message = result.Message;
                    existing.ObservedValue = result.ObservedValue;
                    existing.LastObservedAt = now;
                }
            }
            else if (existing is not null)
            {
                existing.IsActive = false;
                existing.ResolvedAt = now;
                activeAlerts.Remove(existing);
                auditEvents.Add(("monitoring.alert-resolved", endpoint.MachineName, rule.Name));
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var item in auditEvents)
            await audit.LogAsync(item.Event, "system:monitoring", targetMachine: item.Machine, detail: item.Detail, ct: ct);
        foreach (var item in emailEvents)
            await notifications.SendMonitoringAlertAsync(
                item.Machine, item.Rule, item.Severity, item.Detail, item.Recipients, ct);
    }

    internal static EvaluationResult Evaluate(EndpointRegistration endpoint, MonitoringRule rule, DateTimeOffset now)
    {
        var healthAgeMinutes = endpoint.HealthReportedAt.HasValue
            ? (now - endpoint.HealthReportedAt.Value).TotalMinutes
            : double.MaxValue;
        return rule.RuleType switch
        {
            MonitoringRuleType.EndpointOfflineMinutes =>
                Violate(endpoint.LastSeenAt is null || (now - endpoint.LastSeenAt.Value).TotalMinutes > rule.Threshold,
                    endpoint.LastSeenAt is null ? null : Math.Round((now - endpoint.LastSeenAt.Value).TotalMinutes, 1),
                    endpoint.LastSeenAt is null ? "Endpoint has never checked in." : $"Endpoint has been offline for {(int)(now - endpoint.LastSeenAt.Value).TotalMinutes} minutes."),
            MonitoringRuleType.LowSystemDriveFreePercent when healthAgeMinutes <= 30 && endpoint.SystemDriveFreePercent.HasValue =>
                Violate(endpoint.SystemDriveFreePercent.Value < rule.Threshold, endpoint.SystemDriveFreePercent,
                    $"System drive has {endpoint.SystemDriveFreePercent:0.0}% free space."),
            MonitoringRuleType.HighMemoryUsedPercent when healthAgeMinutes <= 30 && endpoint.MemoryUsedPercent.HasValue =>
                Violate(endpoint.MemoryUsedPercent.Value > rule.Threshold, endpoint.MemoryUsedPercent,
                    $"Memory usage is {endpoint.MemoryUsedPercent:0.0}% ."),
            MonitoringRuleType.DefenderDisabled when healthAgeMinutes <= 30 && endpoint.DefenderEnabled.HasValue =>
                Violate(endpoint.DefenderEnabled == false, endpoint.DefenderEnabled == true ? 1 : 0,
                    "Microsoft Defender antivirus or real-time protection is disabled."),
            MonitoringRuleType.DefenderSignatureAgeDays when healthAgeMinutes <= 30 && endpoint.DefenderSignatureAgeDays.HasValue =>
                Violate(endpoint.DefenderSignatureAgeDays.Value > rule.Threshold, endpoint.DefenderSignatureAgeDays,
                    $"Defender signatures are {endpoint.DefenderSignatureAgeDays} days old."),
            MonitoringRuleType.BitLockerNotProtected when healthAgeMinutes <= 30 && endpoint.BitLockerProtected.HasValue =>
                Violate(endpoint.BitLockerProtected == false, endpoint.BitLockerProtected == true ? 1 : 0,
                    "The system volume is not protected by BitLocker."),
            MonitoringRuleType.PendingReboot when healthAgeMinutes <= 30 =>
                Violate(endpoint.PendingReboot, endpoint.PendingReboot ? 1 : 0, "Windows reports a pending reboot."),
            MonitoringRuleType.HealthCollectionFailed when healthAgeMinutes <= 30 =>
                Violate(!string.IsNullOrWhiteSpace(endpoint.HealthCollectionError), null,
                    endpoint.HealthCollectionError ?? "Health collection is working."),
            MonitoringRuleType.ServiceStopped
                when endpoint.ServiceInventoryReportedAt.HasValue
                  && (now - endpoint.ServiceInventoryReportedAt.Value).TotalMinutes <= 30
                  && !string.IsNullOrWhiteSpace(rule.TargetServiceName) =>
                EvaluateService(endpoint.ServicesJson, rule.TargetServiceName),
            _ => new(false, null, string.Empty, false),
        };
    }

    private static EvaluationResult EvaluateService(string servicesJson, string serviceName)
    {
        List<EndpointServiceDto> services;
        try { services = JsonSerializer.Deserialize<List<EndpointServiceDto>>(servicesJson, JsonOpts) ?? []; }
        catch { return new(false, null, string.Empty, false); }

        var service = services.FirstOrDefault(x =>
            string.Equals(x.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        if (service is null)
            return Violate(true, 0, $"Windows service '{serviceName}' was not found.");
        return Violate(!string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase),
            string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            $"Windows service '{serviceName}' is {service.Status} (start mode: {service.StartMode}).");
    }

    private static EvaluationResult Violate(bool condition, double? value, string message) =>
        new(condition, value, message, true);

    private static void ValidateThreshold(MonitoringRuleType type, double threshold)
    {
        var valid = type switch
        {
            MonitoringRuleType.EndpointOfflineMinutes => threshold is >= 5 and <= 1440,
            MonitoringRuleType.LowSystemDriveFreePercent or MonitoringRuleType.HighMemoryUsedPercent => threshold is >= 1 and <= 99,
            MonitoringRuleType.DefenderSignatureAgeDays => threshold is >= 1 and <= 30,
            MonitoringRuleType.ServiceStopped => threshold is >= 0 and <= 1,
            _ => threshold is >= 0 and <= 1,
        };
        if (!valid) throw new ArgumentException("Threshold is outside the allowed range for this rule.");
    }

    private static MonitoringRuleDto MapRule(MonitoringRule x) => new()
    {
        Id = x.Id, RuleType = x.RuleType, Name = x.Name, Description = x.Description,
        Threshold = x.Threshold, Severity = x.Severity, IsEnabled = x.IsEnabled,
        EndpointRegistrationId = x.EndpointRegistrationId, EndpointName = x.Endpoint?.MachineName,
        TargetServiceName = x.TargetServiceName, NotifyByEmail = x.NotifyByEmail,
        NotificationRecipients = DeserializeRecipients(x.NotificationRecipientsJson),
        UpdatedAt = x.UpdatedAt,
    };

    internal static List<MonitoringRule> DefaultRules() =>
    [
        New(MonitoringRuleType.EndpointOfflineMinutes, "Endpoint offline", "Alert when an enabled endpoint misses heartbeats.", 10, MonitoringSeverity.Critical),
        New(MonitoringRuleType.LowSystemDriveFreePercent, "Low system drive space", "Alert when free space on the Windows system drive falls below this percentage.", 15, MonitoringSeverity.Warning),
        New(MonitoringRuleType.HighMemoryUsedPercent, "High memory usage", "Alert when physical memory usage exceeds this percentage.", 90, MonitoringSeverity.Warning),
        New(MonitoringRuleType.DefenderDisabled, "Defender disabled", "Alert when Microsoft Defender antivirus or real-time protection is disabled.", 0, MonitoringSeverity.Critical),
        New(MonitoringRuleType.DefenderSignatureAgeDays, "Defender signatures stale", "Alert when Defender signatures are older than this many days.", 3, MonitoringSeverity.Warning),
        New(MonitoringRuleType.BitLockerNotProtected, "BitLocker not protected", "Alert when the system volume reports BitLocker protection off.", 0, MonitoringSeverity.Warning),
        New(MonitoringRuleType.PendingReboot, "Pending reboot", "Alert when Windows reports that a reboot is required.", 0, MonitoringSeverity.Info),
        New(MonitoringRuleType.HealthCollectionFailed, "Health collection failed", "Alert when core endpoint health metrics cannot be collected.", 0, MonitoringSeverity.Warning),
    ];

    private static MonitoringRule New(MonitoringRuleType type, string name, string description, double threshold, MonitoringSeverity severity) =>
        new() { RuleType = type, Name = name, Description = description, Threshold = threshold, Severity = severity };

    private static string NormalizeServiceName(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length is < 1 or > 256
            || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '$')))
            throw new ArgumentException("Service name must contain only letters, numbers, '.', '-', '_', or '$'.");
        return value;
    }

    private static List<string> NormalizeRecipients(IEnumerable<string> recipients)
    {
        var output = recipients.Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (output.Count > 20) throw new ArgumentException("A rule can have at most 20 email recipients.");
        if (output.Any(x => x.Length > 320 || !MailAddress.TryCreate(x, out _)))
            throw new ArgumentException("One or more notification email addresses are invalid.");
        return output;
    }

    private static List<string> DeserializeRecipients(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    internal sealed record EvaluationResult(bool Violated, double? ObservedValue, string Message, bool HasData);
}
