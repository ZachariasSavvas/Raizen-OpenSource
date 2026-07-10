namespace Raizen.Server.Core.Models;

public sealed class EndpointRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable machine identity (e.g. motherboard UUID or domain SID).</summary>
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Bcrypt hash of the endpoint's API key.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }

    /// <summary>JSON-serialized List&lt;string&gt; for grouping/filtering.</summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>
    /// Set by the dormant-cleanup job when it auto-disables an offline endpoint.
    /// Null when IsEnabled=false was set by an admin (intentional block).
    /// Cleared when the endpoint checks back in and is auto-re-enabled.
    /// </summary>
    public DateTimeOffset? DormantSince { get; set; }

    /// <summary>True when an admin has requested an immediate agent update for this endpoint.</summary>
    public bool UpdatePending { get; set; } = false;

    /// <summary>When the update push was requested. Cleared when the endpoint acknowledges.</summary>
    public DateTimeOffset? UpdateRequestedAt { get; set; }

    public bool PollSigningConfigured { get; set; }
    public DateTimeOffset? LastPollSucceededAt { get; set; }
    public string? LastPollError { get; set; }
    public DateTimeOffset? LastUpdateCheckAt { get; set; }
    public string? LastUpdateStatus { get; set; }
    public string? LastUpdateError { get; set; }
    public DateTimeOffset? LastSuccessfulUpdateAt { get; set; }

    public DateTimeOffset? HealthReportedAt { get; set; }
    public long? UptimeSeconds { get; set; }
    public double? CpuLoadPercent { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public double? SystemDriveFreePercent { get; set; }
    public long? SystemDriveFreeBytes { get; set; }
    public string? LoggedOnUser { get; set; }
    public string IpAddressesJson { get; set; } = "[]";
    public bool PendingReboot { get; set; }
    public bool? DefenderEnabled { get; set; }
    public int? DefenderSignatureAgeDays { get; set; }
    public bool? BitLockerProtected { get; set; }
    public string? HealthCollectionError { get; set; }
    public DateTimeOffset? ProcessInventoryReportedAt { get; set; }
    public DateTimeOffset? ServiceInventoryReportedAt { get; set; }
    public string ProcessesJson { get; set; } = "[]";
    public string ServicesJson { get; set; } = "[]";

    /// <summary>Previous API key hash, valid during grace period after rotation.</summary>
    public string? PreviousApiKeyHash { get; set; }

    /// <summary>When the previous key stops being accepted.</summary>
    public DateTimeOffset? PreviousKeyExpiresAt { get; set; }

    public ICollection<ElevationRequest> Requests { get; set; } = [];
    public ICollection<DiagnosticBundle> DiagnosticBundles { get; set; } = [];
    public ICollection<MonitoringAlert> MonitoringAlerts { get; set; } = [];
    public ICollection<MonitoringRule> MonitoringRules { get; set; } = [];
}
