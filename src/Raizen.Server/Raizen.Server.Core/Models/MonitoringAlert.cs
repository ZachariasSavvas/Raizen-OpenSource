namespace Raizen.Server.Core.Models;

public sealed class MonitoringAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitoringRuleId { get; set; }
    public MonitoringRule Rule { get; set; } = null!;
    public Guid EndpointRegistrationId { get; set; }
    public EndpointRegistration Endpoint { get; set; } = null!;
    public string Message { get; set; } = string.Empty;
    public double? ObservedValue { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset TriggeredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}
