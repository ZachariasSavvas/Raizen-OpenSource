namespace Raizen.Server.Core.Models;

public sealed class AdminUser
{
    public Guid   Id                 { get; set; } = Guid.NewGuid();
    public string Username           { get; set; } = string.Empty;
    /// <summary>V1:{base64(nonce|salt|ciphertext|tag)} — PBKDF2-SHA512 wrapped in AES-256-GCM.</summary>
    public string PasswordHash       { get; set; } = string.Empty;
    public bool   MustChangePassword { get; set; } = true;
    public bool   IsActive           { get; set; } = true;
    public DateTimeOffset  CreatedAt   { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool    TotpEnabled { get; set; } = false;
    /// <summary>T1:{base64(nonce[12] | ciphertext | tag[16])} — AES-256-GCM encrypted Base32 TOTP seed. Null = not configured.</summary>
    public string? TotpSecret  { get; set; }

    /// <summary>
    /// When true, the user must enrol TOTP before MfaDeadline or login is blocked.
    /// False for the seeded Admin account so the first login is never blocked.
    /// </summary>
    public bool RequiresMfa { get; set; } = false;

    /// <summary>
    /// Set on first login after RequiresMfa is enabled. Null = grace period not yet started.
    /// Login is blocked once UtcNow exceeds this value and TotpEnabled is still false.
    /// </summary>
    public DateTimeOffset? MfaDeadline { get; set; }

    /// <summary>Set on every password change. Null = never changed (seeded account).</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Admin, Approver, Operator, or Auditor. Controls which portal features are accessible.</summary>
    public string Role { get; set; } = "Admin";
}
