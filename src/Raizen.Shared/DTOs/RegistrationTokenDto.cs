namespace Raizen.Shared.DTOs;

// ── Token management (admin-facing) ──────────────────────────────────────────

public sealed class CreateRegistrationTokenDto
{
    public string Label      { get; set; } = string.Empty;
    /// <summary>Hours until the token expires. Null means the token never expires.</summary>
    public int?   ExpiryHours { get; set; } = 720;
    /// <summary>Maximum number of endpoint machines that can use this token.</summary>
    public int    MaxUses     { get; set; } = 100;
}

public sealed class RegistrationTokenDto
{
    public Guid           Id        { get; set; }
    public string         Label     { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int            MaxUses   { get; set; }
    public int            UseCount  { get; set; }
    public bool           IsActive  { get; set; }
    public string         CreatedBy { get; set; } = string.Empty;

    /// <summary>True when the token was created with no expiry (sentinel: ExpiresAt > 50 years from now).</summary>
    public bool IsNeverExpires => ExpiresAt > DateTimeOffset.UtcNow.AddYears(50);
    public bool IsExpired  => !IsNeverExpires && DateTimeOffset.UtcNow > ExpiresAt;
    public int  Remaining  => Math.Max(0, MaxUses - UseCount);
}

public sealed class CreateRegistrationTokenResponseDto
{
    public Guid           TokenId        { get; set; }
    /// <summary>Plaintext token — shown once, embed in the agent deployment package.</summary>
    public string         PlaintextToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt      { get; set; }
}

// ── Token exchange (endpoint-facing, called on first boot) ───────────────────

public sealed class ExchangeTokenDto
{
    /// <summary>The plaintext registration token from raizen-config.json.</summary>
    public string  RegistrationToken { get; set; } = string.Empty;
    public string  MachineName       { get; set; } = string.Empty;
    public string? OsVersion         { get; set; }
    public string? AgentVersion      { get; set; }
}

public sealed class ExchangeTokenResponseDto
{
    /// <summary>
    /// The permanent API key for this endpoint.
    /// The endpoint service must save this to raizen-config.json and clear RegistrationToken.
    /// </summary>
    public string ApiKey         { get; set; } = string.Empty;
    public Guid   RegistrationId { get; set; }
}
