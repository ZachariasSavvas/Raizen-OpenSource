namespace Raizen.Server.Core.Models;

/// <summary>
/// Tracks a bulk operation that fans out a single action across multiple endpoints.
/// Each child request links back via ElevationRequest.BulkOperationId.
/// </summary>
public sealed class BulkOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ActionDefinitionId { get; set; }
    public ActionDefinition ActionDefinition { get; set; } = null!;

    public string CreatedByUpn { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
    public string? TicketReference { get; set; }
    public string ParametersJson { get; set; } = "{}";

    public int TotalCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<ElevationRequest> Requests { get; set; } = [];
}
