using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface INotificationService
{
    Task<NotificationSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(NotificationSettings settings, string actorUpn, CancellationToken ct = default);
    Task<string?> TestAsync(CancellationToken ct = default);
    Task SendRequestSubmittedAsync(ElevationRequestDto request, CancellationToken ct = default);
    Task SendRequestReviewedAsync(ElevationRequestDto request, CancellationToken ct = default);
}

public sealed class NotificationService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    ILogger<NotificationService> log,
    IConfiguration config) : INotificationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // AES-256-GCM constants — same pattern as AdminAuthService
    private const int NonceBytes = 12;
    private const int TagBytes   = 16;
    private const string SmtpPasswordPrefix = "S1:";

    private byte[] EncryptionKey()
    {
        var secret = config["Security:EncryptionKey"]
            ?? throw new InvalidOperationException("Security:EncryptionKey is not configured.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// Encrypts a plaintext SMTP password using AES-256-GCM.
    /// Stored format: "S1:{base64(nonce[12] | ciphertext | tag[16])}"
    /// </summary>
    private string EncryptSmtpPassword(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce          = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext     = new byte[plaintextBytes.Length];
        var tag            = new byte[TagBytes];

        using var aes = new AesGcm(EncryptionKey(), TagBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[NonceBytes + plaintextBytes.Length + TagBytes];
        nonce     .CopyTo(combined, 0);
        ciphertext.CopyTo(combined, NonceBytes);
        tag       .CopyTo(combined, NonceBytes + plaintextBytes.Length);

        return SmtpPasswordPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts an SMTP password encrypted by <see cref="EncryptSmtpPassword"/>.
    /// Returns the stored value unchanged if it does not carry the "S1:" prefix
    /// (plaintext migration path — value is treated as-is until the next save).
    /// </summary>
    private string DecryptSmtpPassword(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(SmtpPasswordPrefix, StringComparison.Ordinal)) return stored;

        try
        {
            var combined   = Convert.FromBase64String(stored[SmtpPasswordPrefix.Length..]);
            var secretLen  = combined.Length - NonceBytes - TagBytes;
            if (secretLen <= 0) return string.Empty;

            var nonce      = combined[..NonceBytes];
            var ciphertext = combined[NonceBytes..(NonceBytes + secretLen)];
            var tag        = combined[(NonceBytes + secretLen)..];
            var plaintext  = new byte[secretLen];

            using var aes = new AesGcm(EncryptionKey(), TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Settings CRUD ─────────────────────────────────────────────────────────

    public async Task<NotificationSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.NotificationSettings.FirstOrDefaultAsync(ct);
        return row ?? new NotificationSettings();
    }

    public async Task SaveAsync(NotificationSettings settings, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.NotificationSettings.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            if (!string.IsNullOrEmpty(settings.SmtpPassword))
                settings.SmtpPassword = EncryptSmtpPassword(settings.SmtpPassword);
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            settings.UpdatedBy = actorUpn;
            db.NotificationSettings.Add(settings);
        }
        else
        {
            existing.Enabled          = settings.Enabled;
            existing.SmtpHost         = settings.SmtpHost;
            existing.SmtpPort         = settings.SmtpPort;
            existing.SmtpUseTls       = settings.SmtpUseTls;
            existing.SmtpUsername     = settings.SmtpUsername;
            // Only update password if caller provided a non-empty value; encrypt at rest
            if (!string.IsNullOrEmpty(settings.SmtpPassword))
                existing.SmtpPassword = EncryptSmtpPassword(settings.SmtpPassword);
            existing.FromAddress      = settings.FromAddress;
            existing.FromDisplayName  = settings.FromDisplayName;
            existing.RecipientsJson   = settings.RecipientsJson;
            existing.NotifyOnSubmit   = settings.NotifyOnSubmit;
            existing.NotifyOnApproved = settings.NotifyOnApproved;
            existing.NotifyOnDenied   = settings.NotifyOnDenied;
            existing.UpdatedAt        = DateTimeOffset.UtcNow;
            existing.UpdatedBy        = actorUpn;
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Test connection ───────────────────────────────────────────────────────

    public async Task<string?> TestAsync(CancellationToken ct = default)
    {
        var cfg = await GetAsync(ct);
        if (string.IsNullOrWhiteSpace(cfg.SmtpHost))
            return "SMTP host is not configured.";

        var recipients = GetRecipients(cfg);
        if (recipients.Count == 0)
            return "No recipient email addresses configured.";

        try
        {
            var msg = BuildMessage(cfg,
                subject: "Raizen: Test Email",
                html: "<p>This is a test notification from your <strong>Raizen</strong> server.</p>" +
                      "<p>If you received this, your SMTP settings are working correctly.</p>");

            await SendAsync(cfg, DecryptSmtpPassword(cfg.SmtpPassword), msg, ct);
            return null; // success
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SMTP connection test failed");
            return "Connection test failed. Check server logs for details.";
        }
    }

    // ── Event notifications ───────────────────────────────────────────────────

    public async Task SendRequestSubmittedAsync(ElevationRequestDto req, CancellationToken ct = default)
    {
        var cfg = await GetAsync(ct);
        if (!cfg.Enabled || !cfg.NotifyOnSubmit) return;

        var portalUrl = $"(open the Raizen portal → Requests)";
        var html = $"""
            <table style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#111;width:100%;max-width:620px;border-collapse:collapse">
              <tr><td style="background:#10164A;padding:20px 28px">
                <span style="color:#fff;font-size:18px;font-weight:700">Raizen: Approval Required</span>
              </td></tr>
              <tr><td style="padding:24px 28px">
                <p style="margin:0 0 16px">A new elevation request requires your review.</p>
                <table style="width:100%;border-collapse:collapse;font-size:13px">
                  {Row("Action",        req.ActionDisplayName)}
                  {Row("Requester",     $"{req.RequesterDisplayName} ({req.RequesterUpn})")}
                  {Row("Machine",       req.TargetMachine)}
                  {Row("Justification",req.Justification)}
                  {Row("Submitted",     req.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}
                  {Row("Expires",       req.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))}
                  {Row("Request ID",    req.Id.ToString())}
                </table>
                <p style="margin:20px 0 0;color:#6b7280;font-size:12px">{portalUrl}</p>
              </td></tr>
            </table>
            """;

        await TrySendAsync(cfg, $"[Raizen] Approval Required: {req.ActionDisplayName} on {req.TargetMachine}", html, ct);
    }

    public async Task SendRequestReviewedAsync(ElevationRequestDto req, CancellationToken ct = default)
    {
        var cfg = await GetAsync(ct);
        if (!cfg.Enabled) return;

        bool isApproved = req.Status == Raizen.Shared.Enums.RequestStatus.Approved;
        if (isApproved && !cfg.NotifyOnApproved) return;
        if (!isApproved && !cfg.NotifyOnDenied) return;

        var statusColour = isApproved ? "#059669" : "#dc2626";
        var statusText   = isApproved ? "Approved" : "Denied";
        var html = $"""
            <table style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#111;width:100%;max-width:620px;border-collapse:collapse">
              <tr><td style="background:#10164A;padding:20px 28px">
                <span style="color:#fff;font-size:18px;font-weight:700">Raizen: Request {statusText}</span>
              </td></tr>
              <tr><td style="padding:24px 28px">
                <p style="margin:0 0 16px">
                  An elevation request has been
                  <strong style="color:{statusColour}">{statusText.ToLower()}</strong>.
                </p>
                <table style="width:100%;border-collapse:collapse;font-size:13px">
                  {Row("Action",      req.ActionDisplayName)}
                  {Row("Requester",   req.RequesterUpn)}
                  {Row("Machine",     req.TargetMachine)}
                  {Row("Reviewed by", req.ReviewerUpn ?? "—")}
                  {(string.IsNullOrEmpty(req.ReviewerNote) ? "" : Row("Note", req.ReviewerNote))}
                  {Row("Request ID",  req.Id.ToString())}
                </table>
              </td></tr>
            </table>
            """;

        await TrySendAsync(cfg, $"[Raizen] Request {statusText}: {req.ActionDisplayName} on {req.TargetMachine}", html, ct);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task TrySendAsync(NotificationSettings cfg, string subject, string html, CancellationToken ct)
    {
        try
        {
            var msg = BuildMessage(cfg, subject, html);
            await SendAsync(cfg, DecryptSmtpPassword(cfg.SmtpPassword), msg, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to send notification email '{Subject}'.", subject);
        }
    }

    private static MimeMessage BuildMessage(NotificationSettings cfg, string subject, string html)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(cfg.FromDisplayName, cfg.FromAddress));
        msg.Subject = subject;

        foreach (var addr in GetRecipients(cfg))
            msg.To.Add(MailboxAddress.Parse(addr));

        msg.Body = new TextPart(MimeKit.Text.TextFormat.Html) { Text = html };
        return msg;
    }

    private static async Task SendAsync(NotificationSettings cfg, string smtpPassword, MimeMessage msg, CancellationToken ct)
    {
        using var smtp = new SmtpClient();

        var secureSocketOptions = cfg.SmtpUseTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await smtp.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, secureSocketOptions, ct);

        if (!string.IsNullOrEmpty(cfg.SmtpUsername))
            await smtp.AuthenticateAsync(cfg.SmtpUsername, smtpPassword, ct);

        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }

    private static List<string> GetRecipients(NotificationSettings cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.RecipientsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(cfg.RecipientsJson, JsonOpts) ?? []; }
        catch { return []; }
    }

    private static string Row(string label, string value) =>
        "<tr>" +
        $"<td style=\"padding:6px 12px 6px 0;color:#6b7280;white-space:nowrap;vertical-align:top\">{label}</td>" +
        $"<td style=\"padding:6px 0;font-weight:600\">{System.Net.WebUtility.HtmlEncode(value)}</td>" +
        "</tr>";
}
