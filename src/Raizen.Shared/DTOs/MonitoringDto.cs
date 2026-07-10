using Raizen.Shared.Enums;

namespace Raizen.Shared.DTOs;

public sealed class MonitoringRuleDto
{
    public Guid Id { get; set; }
    public MonitoringRuleType RuleType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public MonitoringSeverity Severity { get; set; }
    public bool IsEnabled { get; set; }
    public Guid? EndpointRegistrationId { get; set; }
    public string? EndpointName { get; set; }
    public string? TargetServiceName { get; set; }
    public bool NotifyByEmail { get; set; }
    public List<string> NotificationRecipients { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ServiceMonitoringRuleUpsertDto
{
    public Guid? EndpointRegistrationId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public MonitoringSeverity Severity { get; set; } = MonitoringSeverity.Critical;
    public bool IsEnabled { get; set; } = true;
    public bool NotifyByEmail { get; set; }
    public List<string> NotificationRecipients { get; set; } = [];
}

public sealed class MonitoringAlertDto
{
    public Guid Id { get; set; }
    public Guid EndpointRegistrationId { get; set; }
    public Guid MonitoringRuleId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public MonitoringRuleType RuleType { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public MonitoringSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public double? ObservedValue { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}
