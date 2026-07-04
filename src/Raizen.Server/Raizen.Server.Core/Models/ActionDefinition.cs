using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Models;

public sealed class ActionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ActionType ActionType { get; set; }

    /// <summary>JSON-serialized List&lt;ActionParameterDefinition&gt;.</summary>
    public string ParametersSchemaJson { get; set; } = "[]";

    /// <summary>JSON-serialized List&lt;string&gt; of Entra ID group ObjectIds for approvers.</summary>
    public string ApproverGroupIdsJson { get; set; } = "[]";

    public bool AutoApprove { get; set; }
    public string? AutoApproveConditionDescription { get; set; }
    public int ApprovalWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Number of independent approvals required before the request is dispatched.
    /// 1 = standard single-approver (default). 2 = four-eyes principle.
    /// </summary>
    public int MinApprovers { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByUpn { get; set; } = string.Empty;

    public ICollection<ElevationRequest> Requests { get; set; } = [];
}
