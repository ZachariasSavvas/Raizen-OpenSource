using Raizen.Shared.Enums;

namespace Raizen.Shared.DTOs;

public sealed class ActionDefinitionDto
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ActionType ActionType { get; set; }

    /// <summary>Ordered list of parameters the requester must supply.</summary>
    public List<ActionParameterDefinitionDto> Parameters { get; set; } = [];

    /// <summary>Entra ID group ObjectIds whose members may approve this action.</summary>
    public List<string> ApproverGroupIds { get; set; } = [];

    /// <summary>If true, matching requests skip manual approval (auto-approve).</summary>
    public bool AutoApprove { get; set; }
    public string? AutoApproveConditionDescription { get; set; }

    /// <summary>Maximum lifetime of an approved-but-not-yet-executed request.</summary>
    public int ApprovalWindowMinutes { get; set; } = 60;

    /// <summary>Number of independent approvals required (1 = standard, 2 = four-eyes).</summary>
    public int MinApprovers { get; set; } = 1;

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Average seconds from submission to approval (null if insufficient data).</summary>
    public int? AverageApprovalSeconds { get; set; }
}

public sealed class ActionParameterDefinitionDto
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ParameterType Type { get; set; }
    public bool Required { get; set; } = true;

    /// <summary>
    /// Regex or allowlist used to validate the parameter value server-side.
    /// Example: "^[A-Za-z0-9._-]{1,64}$" for a service name.
    /// </summary>
    public string? ValidationPattern { get; set; }
    public string? DefaultValue { get; set; }
}

public sealed class UpsertActionDefinitionDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ActionType ActionType { get; set; }
    public List<ActionParameterDefinitionDto> Parameters { get; set; } = [];
    public List<string> ApproverGroupIds { get; set; } = [];
    public bool AutoApprove { get; set; }
    public string? AutoApproveConditionDescription { get; set; }
    public int ApprovalWindowMinutes { get; set; } = 60;
    /// <summary>Number of independent approvals required (1 = standard, 2 = four-eyes).</summary>
    public int MinApprovers { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
}
