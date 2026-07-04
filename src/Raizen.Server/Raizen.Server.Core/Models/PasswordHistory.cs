namespace Raizen.Server.Core.Models;

public sealed class PasswordHistory
{
    public long Id { get; set; }
    public Guid AdminUserId { get; set; }
    /// <summary>V1:{base64(...)} — same format as AdminUser.PasswordHash.</summary>
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
