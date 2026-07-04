using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Models;

public sealed class ElevationRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ActionDefinitionId { get; set; }
    public ActionDefinition ActionDefinition { get; set; } = null!;

    public Guid EndpointRegistrationId { get; set; }
    public EndpointRegistration Endpoint { get; set; } = null!;

    public string RequesterUpn { get; set; } = string.Empty;
    public string RequesterDisplayName { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public string? TicketReference { get; set; }

    /// <summary>JSON-serialized Dictionary&lt;string,string&gt; validated against ActionDefinition.Parameters.</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>Immutable copy of ParametersJson as submitted. Preserved when an approver overrides parameters.</summary>
    public string? OriginalParametersJson { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>If set, the approved request will not be released for execution until this time.</summary>
    public DateTimeOffset? ScheduledForUtc { get; set; }

    /// <summary>FK to parent bulk operation, if this request was created as part of a bulk submission.</summary>
    public Guid? BulkOperationId { get; set; }
    public BulkOperation? BulkOperation { get; set; }

    public string? ReviewerUpn { get; set; }
    public string? ReviewerNote { get; set; }

    public string? ExecutionResult { get; set; }
    public string? ExecutionError { get; set; }

    /// <summary>Concurrency token — maps to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }

    public ICollection<AuditLog> AuditLogs { get; set; } = [];
    public ICollection<RequestApproval> Approvals { get; set; } = [];
}
