using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Models;

public sealed class MonitoringRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MonitoringRuleType RuleType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public MonitoringSeverity Severity { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDeleted { get; set; }
    public Guid? EndpointRegistrationId { get; set; }
    public EndpointRegistration? Endpoint { get; set; }
    public string? TargetServiceName { get; set; }
    public bool NotifyByEmail { get; set; }
    public string NotificationRecipientsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<MonitoringAlert> Alerts { get; set; } = [];
}
