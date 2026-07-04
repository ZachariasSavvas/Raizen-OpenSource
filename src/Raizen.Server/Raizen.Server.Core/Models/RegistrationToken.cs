namespace Raizen.Server.Core.Models;

public sealed class RegistrationToken
{
    public Guid           Id        { get; set; } = Guid.NewGuid();

    /// <summary>SHA-512 hex hash of the plaintext token (same scheme as ApiKeyHash).</summary>
    public string         TokenHash { get; set; } = string.Empty;

    public string         Label     { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public int            MaxUses   { get; set; } = 100;
    public int            UseCount  { get; set; } = 0;
    public bool           IsActive  { get; set; } = true;
    public string         CreatedBy { get; set; } = string.Empty;
}
