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
}

public sealed class PushUpdateDto
{
    /// <summary>
    /// Specific endpoint registration IDs to push the update to.
    /// Null or empty means all enabled endpoints.
    /// </summary>
    public List<Guid>? RegistrationIds { get; set; }
}
