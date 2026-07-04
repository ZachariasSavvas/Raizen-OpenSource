using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OtpNet;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;

namespace Raizen.Server.Core.Services;

public record TotpSetupInfo(string Base32Secret, string OtpAuthUri);

public interface IAdminAuthService
{
    Task<AdminUser?> ValidateAsync(string username, string password, CancellationToken ct = default);
    Task<AdminUser>  CreateAsync(string username, string password, bool mustChangePassword = false, bool requiresMfa = false, string role = "Admin", CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);
    /// <summary>Returns the role claims for the given role string, following the hierarchy: Admin > Approver > Operator > Auditor.</summary>
    static List<System.Security.Claims.Claim> GetRoleClaims(string role)
    {
        var claims = new List<System.Security.Claims.Claim>();
        switch (role)
        {
            case "Admin":
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Admin"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Approver"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Operator"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Auditor"));
                break;
            case "Approver":
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Approver"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Operator"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Auditor"));
                break;
            case "Operator":
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Operator"));
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Auditor"));
                break;
            default: // Auditor
                claims.Add(new(System.Security.Claims.ClaimTypes.Role, "Raizen.Auditor"));
                break;
        }
        return claims;
    }

    static (bool Valid, string? Error) ValidatePasswordPolicy(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            return (false, "Password must be at least 12 characters.");
        if (!password.Any(char.IsUpper))
            return (false, "Password must contain at least one uppercase letter.");
        if (!password.Any(char.IsLower))
            return (false, "Password must contain at least one lowercase letter.");
        if (!password.Any(char.IsDigit))
            return (false, "Password must contain at least one digit.");
        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            return (false, "Password must contain at least one special character.");
        return (true, null);
    }
    Task<List<AdminUser>> ListAsync(CancellationToken ct = default);
    Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);
    Task SetRequiresMfaAsync(Guid userId, bool requiresMfa, CancellationToken ct = default);

    /// <summary>Generates a new TOTP secret for the user (stored but not yet enabled) and returns setup info.</summary>
    Task<TotpSetupInfo> GenerateTotpSetupAsync(Guid userId, string username, CancellationToken ct = default);
    /// <summary>Validates the provided code against the pending secret and enables TOTP if correct.</summary>
    Task<bool> ConfirmTotpAsync(Guid userId, string code, CancellationToken ct = default);
    /// <summary>Validates a TOTP code for an already-enabled user (used during login step 2).</summary>
    Task<bool> VerifyTotpAsync(Guid userId, string code, CancellationToken ct = default);
    /// <summary>Disables TOTP and clears the secret for the given user.</summary>
    Task DisableTotpAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Password security: PBKDF2-SHA512 (100 000 iterations, 32-byte output, 16-byte random salt)
/// wrapped in AES-256-GCM (12-byte nonce, 16-byte tag) keyed with an app-level secret.
/// Storage format: "V1:{base64(nonce[12] | salt[16] | ciphertext[32] | tag[16])}"
/// </summary>
public sealed class AdminAuthService(IDbContextFactory<RaizenDbContext> dbFactory, IConfiguration config) : IAdminAuthService
{
    private const int Iterations = 100_000;
    private const int HashBytes  = 32;
    private const int SaltBytes  = 16;
    private const int NonceBytes = 12;
    private const int TagBytes   = 16;

    private byte[] AppKey()
    {
        var secret = config["Security:EncryptionKey"]
            ?? throw new InvalidOperationException("Security:EncryptionKey is not configured.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    private string HashPassword(string password)
    {
        var salt      = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash      = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        var plaintext = new byte[SaltBytes + HashBytes];
        salt.CopyTo(plaintext, 0);
        hash.CopyTo(plaintext, SaltBytes);

        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[TagBytes];

        using var aes = new AesGcm(AppKey(), TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var combined = new byte[NonceBytes + plaintext.Length + TagBytes];
        nonce     .CopyTo(combined, 0);
        ciphertext.CopyTo(combined, NonceBytes);
        tag       .CopyTo(combined, NonceBytes + plaintext.Length);

        return "V1:" + Convert.ToBase64String(combined);
    }

    private bool VerifyPassword(string password, string stored)
    {
        try
        {
            if (!stored.StartsWith("V1:")) return false;
            var combined     = Convert.FromBase64String(stored[3..]);
            var plaintextLen = SaltBytes + HashBytes;

            var nonce      = combined[..NonceBytes];
            var ciphertext = combined[NonceBytes..(NonceBytes + plaintextLen)];
            var tag        = combined[(NonceBytes + plaintextLen)..];
            var plaintext  = new byte[plaintextLen];

            using var aes = new AesGcm(AppKey(), TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var storedSalt = plaintext[..SaltBytes];
            var storedHash = plaintext[SaltBytes..];
            var candidate  = Rfc2898DeriveBytes.Pbkdf2(password, storedSalt, Iterations, HashAlgorithmName.SHA512, HashBytes);
            return CryptographicOperations.FixedTimeEquals(candidate, storedHash);
        }
        catch { return false; }
    }

    public async Task<AdminUser?> ValidateAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);
        if (user is null || !VerifyPassword(password, user.PasswordHash)) return null;

        user.LastLoginAt = DateTimeOffset.UtcNow;

        // Start the MFA grace period on the first login after RequiresMfa is enabled.
        if (user.RequiresMfa && !user.TotpEnabled && user.MfaDeadline is null)
        {
            var graceDays = int.TryParse(config["Security:MfaGracePeriodDays"], out var d) ? d : 7;
            user.MfaDeadline = DateTimeOffset.UtcNow.AddDays(graceDays);
        }

        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<AdminUser> CreateAsync(string username, string password,
        bool mustChangePassword = false, bool requiresMfa = false, string role = "Admin", CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = new AdminUser
        {
            Username           = username,
            PasswordHash       = HashPassword(password),
            MustChangePassword = mustChangePassword,
            RequiresMfa        = requiresMfa,
            Role               = role,
        };
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task SetRequiresMfaAsync(Guid userId, bool requiresMfa, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");
        user.RequiresMfa = requiresMfa;
        // Reset the deadline so the grace period starts fresh on next login.
        if (requiresMfa && !user.TotpEnabled)
            user.MfaDeadline = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");

        // Check password history — prevent reuse of last 5 passwords
        var recent = await db.PasswordHistories
            .Where(h => h.AdminUserId == userId)
            .OrderByDescending(h => h.CreatedAt)
            .Take(5)
            .Select(h => h.PasswordHash)
            .ToListAsync(ct);
        foreach (var oldHash in recent)
        {
            if (VerifyPassword(newPassword, oldHash))
                throw new InvalidOperationException("Cannot reuse any of your last 5 passwords.");
        }
        // Also check the current password
        if (VerifyPassword(newPassword, user.PasswordHash))
            throw new InvalidOperationException("Cannot reuse any of your last 5 passwords.");

        // Save current hash to history before replacing
        db.PasswordHistories.Add(new PasswordHistory
        {
            AdminUserId  = userId,
            PasswordHash = user.PasswordHash,
            CreatedAt    = DateTimeOffset.UtcNow,
        });

        user.PasswordHash       = HashPassword(newPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt  = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminUser>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AdminUsers.OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    // ── TOTP ─────────────────────────────────────────────────────────────────

    private string EncryptTotpSecret(byte[] secretBytes)
    {
        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[secretBytes.Length];
        var tag        = new byte[TagBytes];
        using var aes  = new AesGcm(AppKey(), TagBytes);
        aes.Encrypt(nonce, secretBytes, ciphertext, tag);
        var combined = new byte[NonceBytes + secretBytes.Length + TagBytes];
        nonce     .CopyTo(combined, 0);
        ciphertext.CopyTo(combined, NonceBytes);
        tag       .CopyTo(combined, NonceBytes + secretBytes.Length);
        return "T1:" + Convert.ToBase64String(combined);
    }

    private byte[]? DecryptTotpSecret(string? stored)
    {
        if (stored is null || !stored.StartsWith("T1:")) return null;
        try
        {
            var combined   = Convert.FromBase64String(stored[3..]);
            var secretLen  = combined.Length - NonceBytes - TagBytes;
            if (secretLen <= 0) return null;
            var nonce      = combined[..NonceBytes];
            var ciphertext = combined[NonceBytes..(NonceBytes + secretLen)];
            var tag        = combined[(NonceBytes + secretLen)..];
            var plaintext  = new byte[secretLen];
            using var aes  = new AesGcm(AppKey(), TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch { return null; }
    }

    private static bool ValidateTotp(byte[] secretBytes, string code)
    {
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        var totp = new Totp(secretBytes, step: 30, totpSize: 6);
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    public async Task<TotpSetupInfo> GenerateTotpSetupAsync(Guid userId, string username, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");

        var secretBytes   = KeyGeneration.GenerateRandomKey(20); // 160-bit TOTP seed
        var base32Secret  = Base32Encoding.ToString(secretBytes);
        var issuer        = "Raizen";
        var label         = Uri.EscapeDataString($"{issuer}:{username}");
        var otpAuthUri    = $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";

        // Store encrypted but leave TotpEnabled=false until ConfirmTotpAsync
        user.TotpSecret  = EncryptTotpSecret(secretBytes);
        user.TotpEnabled = false;
        await db.SaveChangesAsync(ct);

        return new TotpSetupInfo(base32Secret, otpAuthUri);
    }

    public async Task<bool> ConfirmTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");

        var secretBytes = DecryptTotpSecret(user.TotpSecret);
        if (secretBytes is null) return false;
        if (!ValidateTotp(secretBytes, code)) return false;

        user.TotpEnabled = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> VerifyTotpAsync(Guid userId, string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct);
        if (user is null || !user.TotpEnabled) return false;
        var secretBytes = DecryptTotpSecret(user.TotpSecret);
        if (secretBytes is null) return false;
        return ValidateTotp(secretBytes, code);
    }

    public async Task DisableTotpAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("User not found.");
        user.TotpEnabled = false;
        user.TotpSecret  = null;
        await db.SaveChangesAsync(ct);
    }
}
