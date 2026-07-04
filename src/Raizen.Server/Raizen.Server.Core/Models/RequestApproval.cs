namespace Raizen.Server.Core.Models;

/// <summary>
/// Records a single approver's vote (approve or deny) on an elevation request.
/// Used when an ActionDefinition has MinApprovers > 1 (four-eyes principle).
/// </summary>
public sealed class RequestApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public ElevationRequest Request { get; set; } = null!;

    public string ApproverUpn { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>True = approved, False = denied.</summary>
    public bool Approved { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
