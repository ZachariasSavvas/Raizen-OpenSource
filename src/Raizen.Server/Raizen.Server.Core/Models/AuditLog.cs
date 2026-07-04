namespace Raizen.Server.Core.Models;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RequestId { get; set; }
    public ElevationRequest? Request { get; set; }

    /// <summary>
    /// Structured event name, e.g.:
    ///   request.submitted  request.approved  request.denied
    ///   request.executing  request.succeeded  request.failed
    ///   action.created  action.updated  action.disabled
    ///   endpoint.registered  endpoint.heartbeat  endpoint.disabled
    ///   auth.failed
    /// </summary>
    public string Event { get; set; } = string.Empty;
    public string ActorUpn { get; set; } = string.Empty;
    public string? TargetMachine { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Tamper-detection hash chain ──────────────────────────────────────────
    /// <summary>RowHash of the immediately preceding audit log entry (by OccurredAt, then Id).
    /// Empty string for the first entry ever.</summary>
    public string? PreviousHash { get; set; }

    /// <summary>HMAC-SHA256 over all fields of this row including PreviousHash.
    /// Null for entries created before the hash chain feature was enabled.</summary>
    public string? RowHash { get; set; }
}
