using Raizen.Shared.Enums;

namespace Raizen.Shared.DTOs;

/// <summary>Payload for submitting a bulk operation across multiple endpoints.</summary>
public sealed class SubmitBulkOperationDto
{
    public Guid ActionDefinitionId { get; set; }
    public string Justification { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
    public string? TicketReference { get; set; }

    /// <summary>Specific endpoint registration IDs to target.</summary>
    public List<Guid> EndpointIds { get; set; } = [];

    /// <summary>Alternative: target all enabled endpoints matching this tag (from TagsJson).</summary>
    public string? EndpointTag { get; set; }
}

/// <summary>Read-only view of a bulk operation with progress summary.</summary>
public sealed class BulkOperationDto
{
    public Guid Id { get; set; }
    public Guid ActionDefinitionId { get; set; }
    public string ActionDisplayName { get; set; } = string.Empty;
    public ActionType ActionType { get; set; }
    public string CreatedByUpn { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public string? TicketReference { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = [];

    public int TotalCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount => TotalCount - SucceededCount - FailedCount;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Child request summaries (populated on detail view).</summary>
    public List<BulkChildRequestDto> Requests { get; set; } = [];
}

/// <summary>Summary of a child request within a bulk operation.</summary>
public sealed class BulkChildRequestDto
{
    public Guid RequestId { get; set; }
    public string TargetMachine { get; set; } = string.Empty;
    public RequestStatus Status { get; set; }
    public string? ExecutionResult { get; set; }
    public string? ExecutionError { get; set; }
}
