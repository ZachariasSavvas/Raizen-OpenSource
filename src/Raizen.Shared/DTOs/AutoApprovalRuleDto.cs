using Raizen.Shared.Enums;

namespace Raizen.Shared.DTOs;

public sealed class AutoApprovalRuleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ActionType? ActionType { get; set; }
    public Guid? ActionDefinitionId { get; set; }
    public string? RequesterUpnPattern { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CreateAutoApprovalRuleDto
{
    public string Name { get; set; } = string.Empty;
    public ActionType? ActionType { get; set; }
    public Guid? ActionDefinitionId { get; set; }
    public string? RequesterUpnPattern { get; set; }
    public bool IsEnabled { get; set; } = true;
}
