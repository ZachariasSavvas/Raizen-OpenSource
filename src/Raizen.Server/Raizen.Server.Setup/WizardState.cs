using System.Security.Cryptography;
using Raizen.Server.Setup.Installer;

namespace Raizen.Server.Setup;

public class WizardState
{
    public WizardState() => ConfigWriter.LoadExistingSecrets(this);

    // ── Database ──────────────────────────────────────────────────────────────
    public string PgHost          { get; set; } = "localhost";
    public int    PgPort          { get; set; } = 5432;
    public string PgSuperUser     { get; set; } = "postgres";
    public string PgSuperPass     { get; set; } = "";
    public string DbPass          { get; set; } = GeneratePassword(20);

    // True when a bundled installer was found and PostgreSQL needs to be installed
    public bool   PgNeedsInstall  { get; set; } = false;
    public string PgInstallerPath { get; set; } = "";

    // ── Server ────────────────────────────────────────────────────────────────
    public int    ApiPort       { get; set; } = 5001;
    public int    WebPort       { get; set; } = 5002;
    public string ServerHostname { get; set; } = Environment.MachineName;
    public string PublicApiUrl  { get; set; } = $"https://{Environment.MachineName}:5001";
    public string EncryptionKey { get; set; } = GenerateBase64Key(32);
    public string AuditHmacKey  { get; set; } = GenerateBase64Key(32);
    public string? ExistingConfigError { get; set; }

    // ── TLS certificate (generated during install) ────────────────────────
    public string CertPassword        { get; set; } = GeneratePassword(20);
    /// <summary>SHA-256 hex thumbprint — matches what the agent's TLS pinning callback uses.</summary>
    public string CertThumbprintSha256 { get; set; } = "";
    public string CertDir             { get; } = @"C:\Program Files\Raizen\Server\certs";
    public string CertPfxPath    => Path.Combine(CertDir, "raizen.pfx");

    // ── RSA poll signing key (generated during install) ───────────────────
    public string PollSigningPrivateKeyPem { get; set; } = "";
    public string PollSigningPublicKeyPem  { get; set; } = "";

    // ── Install paths (resolved at install time) ───────────────────────────
    public string ApiInstallDir      { get; } = @"C:\Program Files\Raizen\Server\Api";
    public string WebInstallDir      { get; } = @"C:\Program Files\Raizen\Server\Web";
    public string PackagesInstallDir { get; } = @"C:\Program Files\Raizen\Server\Packages";

    // Set to the copied MSI path during installation (empty if MSI not present)
    public string AgentMsiInstallPath { get; set; } = "";

    // ── Result ────────────────────────────────────────────────────────────────
    public bool   InstallSuccess { get; set; }
    public string? ErrorMessage  { get; set; }

    public static string GeneratePassword(int length)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    public static string GenerateBase64Key(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));
}
