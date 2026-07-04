namespace Raizen.Server.Core.Models;

/// <summary>
/// Single-row table that stores the global SMTP and notification configuration.
/// Always read/written via <see cref="Services.INotificationService"/>.
/// </summary>
public sealed class NotificationSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Master switch — no emails sent when false.</summary>
    public bool Enabled { get; set; } = false;

    // ── SMTP ──────────────────────────────────────────────────────────────────
    public string SmtpHost     { get; set; } = string.Empty;
    public int    SmtpPort     { get; set; } = 587;
    public bool   SmtpUseTls   { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>
    /// Stored as plaintext (app password / service account credential).
    /// Protected by database-level access controls.
    /// </summary>
    public string SmtpPassword { get; set; } = string.Empty;

    public string FromAddress     { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = "Raizen";

    /// <summary>JSON array of recipient email address strings.</summary>
    public string RecipientsJson { get; set; } = "[]";

    // ── Notification event toggles ────────────────────────────────────────────
    public bool NotifyOnSubmit   { get; set; } = true;
    public bool NotifyOnApproved { get; set; } = true;
    public bool NotifyOnDenied   { get; set; } = true;

    // ── Audit ─────────────────────────────────────────────────────────────────
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string         UpdatedBy { get; set; } = string.Empty;
}
