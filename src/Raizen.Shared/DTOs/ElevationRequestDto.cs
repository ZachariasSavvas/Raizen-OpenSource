using Raizen.Shared.Enums;

namespace Raizen.Shared.DTOs;

public sealed class ElevationRequestDto
{
    public Guid Id { get; set; }
    public Guid ActionDefinitionId { get; set; }
    public string ActionDisplayName { get; set; } = string.Empty;
    public ActionType ActionType { get; set; }

    /// <summary>UPN of the user who submitted the request.</summary>
    public string RequesterUpn { get; set; } = string.Empty;
    public string RequesterDisplayName { get; set; } = string.Empty;

    /// <summary>Internal registration ID of the endpoint this request belongs to.</summary>
    public Guid EndpointRegistrationId { get; set; }

    /// <summary>FQDN or NetBIOS name of the machine where the action will run.</summary>
    public string TargetMachine { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Business justification provided by requester.</summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>Key-value pairs matching the ActionDefinition's parameter schema.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    public RequestStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>If set, execution is deferred until this time.</summary>
    public DateTimeOffset? ScheduledForUtc { get; set; }

    /// <summary>Parent bulk operation ID, if this request is part of a bulk submission.</summary>
    public Guid? BulkOperationId { get; set; }

    /// <summary>UPN of the approver or denier.</summary>
    public string? ReviewerUpn { get; set; }
    public string? ReviewerNote { get; set; }
    public string? ExecutionResult { get; set; }
    public string? ExecutionError { get; set; }

    /// <summary>Ticket reference (ServiceNow, Jira, etc.) for audit linkage.</summary>
    public string? TicketReference { get; set; }

    /// <summary>Number of approvals required for this action (from ActionDefinition.MinApprovers).</summary>
    public int MinApprovers { get; set; } = 1;

    /// <summary>Individual approver votes recorded so far.</summary>
    public List<RequestApprovalDto> Approvals { get; set; } = [];
}

public sealed class RequestApprovalDto
{
    public Guid Id { get; set; }
    public string ApproverUpn { get; set; } = string.Empty;
    public string? Note { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Payload sent by the tray app to submit a new elevation request.</summary>
public sealed class SubmitElevationRequestDto
{
    public Guid ActionDefinitionId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
    public string? TicketReference { get; set; }

    /// <summary>If set, request execution will be deferred until this UTC time.</summary>
    public DateTimeOffset? ScheduledForUtc { get; set; }

    /// <summary>
    /// UPN of the Windows user submitting the request.
    /// Dedicated field — preferred over the legacy __requester_upn parameter key.
    /// </summary>
    public string? RequesterUpn { get; set; }

    /// <summary>
    /// Display name of the Windows user submitting the request.
    /// Dedicated field — preferred over the legacy __requester_display_name parameter key.
    /// </summary>
    public string? RequesterDisplayName { get; set; }
}

/// <summary>Payload sent by the endpoint service when reporting execution outcome.</summary>
public sealed class ExecutionResultDto
{
    public Guid RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ResultMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
}

/// <summary>Payload used by approvers to review a request.</summary>
public sealed class ReviewRequestDto
{
    public bool Approved { get; set; }
    public string? Note { get; set; }
    /// <summary>Approver may override parameters (e.g. restrict scope) before approval.</summary>
    public Dictionary<string, string>? OverrideParameters { get; set; }
}
