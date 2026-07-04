using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Models;

public sealed class AutoApprovalRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>If set, the rule only applies to requests for this action type.</summary>
    public ActionType? ActionType { get; set; }

    /// <summary>If set, the rule only applies to this specific action definition.</summary>
    public Guid? ActionDefinitionId { get; set; }

    /// <summary>Glob pattern for the requester UPN. Null matches any requester. Supports * wildcard.</summary>
    public string? RequesterUpnPattern { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
