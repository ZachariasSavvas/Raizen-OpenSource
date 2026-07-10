namespace Raizen.Shared.DTOs;

public sealed class EndpointRegistrationDto
{
    public Guid Id { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public List<string> Tags { get; set; } = [];

    /// <summary>Non-null when the endpoint was auto-disabled by the dormant-cleanup job.</summary>
    public DateTimeOffset? DormantSince { get; set; }

    /// <summary>True when an admin has requested an immediate agent update for this endpoint.</summary>
    public bool UpdatePending { get; set; }

    /// <summary>When the update push was requested.</summary>
    public DateTimeOffset? UpdateRequestedAt { get; set; }

    public bool PollSigningConfigured { get; set; }
    public DateTimeOffset? LastPollSucceededAt { get; set; }
    public string? LastPollError { get; set; }
    public DateTimeOffset? LastUpdateCheckAt { get; set; }
    public string? LastUpdateStatus { get; set; }
    public string? LastUpdateError { get; set; }
    public DateTimeOffset? LastSuccessfulUpdateAt { get; set; }
    public EndpointHealthSnapshotDto? Health { get; set; }
}

public sealed class RegisterEndpointDto
{
    public string MachineName { get; set; } = string.Empty;
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
}

public sealed class EndpointHeartbeatDto
{
    public string AgentVersion { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public bool PollSigningConfigured { get; set; }
    public DateTimeOffset? LastPollSucceededAt { get; set; }
    public string? LastPollError { get; set; }
    public DateTimeOffset? LastUpdateCheckAt { get; set; }
    public string? LastUpdateStatus { get; set; }
    public string? LastUpdateError { get; set; }
    public DateTimeOffset? LastSuccessfulUpdateAt { get; set; }
    public EndpointHealthSnapshotDto? Health { get; set; }
}

public sealed class EndpointHealthSnapshotDto
{
    public DateTimeOffset CollectedAt { get; set; }
    public long UptimeSeconds { get; set; }
    public double? CpuLoadPercent { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public double? SystemDriveFreePercent { get; set; }
    public long? SystemDriveFreeBytes { get; set; }
    public string? LoggedOnUser { get; set; }
    public List<string> IpAddresses { get; set; } = [];
    public bool PendingReboot { get; set; }
    public bool? DefenderEnabled { get; set; }
    public int? DefenderSignatureAgeDays { get; set; }
    public bool? BitLockerProtected { get; set; }
    public bool ProcessesCollected { get; set; }
    public bool ServicesCollected { get; set; }
    public List<EndpointProcessDto> Processes { get; set; } = [];
    public List<EndpointServiceDto> Services { get; set; } = [];
    public string? CollectionError { get; set; }
}

public sealed class EndpointProcessDto
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long WorkingSetBytes { get; set; }
    public double TotalProcessorTimeSeconds { get; set; }
    public int SessionId { get; set; }
}

public sealed class EndpointServiceDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartMode { get; set; } = string.Empty;
}

public sealed class PushUpdateDto
{
    /// <summary>
    /// Specific endpoint registration IDs to push the update to.
    /// Null or empty means all enabled endpoints.
    /// </summary>
    public List<Guid>? RegistrationIds { get; set; }
}
