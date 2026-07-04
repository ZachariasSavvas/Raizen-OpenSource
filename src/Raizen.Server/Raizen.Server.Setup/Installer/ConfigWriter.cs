using System.Text.Json;

namespace Raizen.Server.Setup.Installer;

public static class ConfigWriter
{
    private static readonly JsonSerializerOptions _opts =
        new() { WriteIndented = true };

    public static void WriteApiConfig(WizardState s)
    {
        var config = new
        {
            Kestrel = new
            {
                Endpoints = new
                {
                    HttpsDefault = new
                    {
                        Url = $"https://0.0.0.0:{s.ApiPort}",
                        Certificate = new { Path = s.CertPfxPath, Password = s.CertPassword }
                    }
                }
            },
            ConnectionStrings = new
            {
                Default = $"Host={s.PgHost};Port={s.PgPort};Database=raizen;Username=raizen;Password={s.DbPass}"
            },
            Security = new
            {
                PollSigningPrivateKeyPem = s.PollSigningPrivateKeyPem,
                AuditHmacKey            = s.AuditHmacKey,
            },
            AzureAd = new
            {
                TenantId = "not-configured",
                ClientId = "not-configured",
                Audience = "not-configured"
            },
            AllowedHosts = "*"
        };
        Write(Path.Combine(s.ApiInstallDir, "appsettings.Production.json"), config);
    }

    public static void WriteWebConfig(WizardState s)
    {
        var config = new
        {
            Kestrel = new
            {
                Endpoints = new
                {
                    HttpsDefault = new
                    {
                        Url = $"https://0.0.0.0:{s.WebPort}",
                        Certificate = new { Path = s.CertPfxPath, Password = s.CertPassword }
                    }
                }
            },
            ConnectionStrings = new
            {
                Default = $"Host={s.PgHost};Port={s.PgPort};Database=raizen;Username=raizen;Password={s.DbPass}"
            },
            Security = new
            {
                EncryptionKey            = s.EncryptionKey,
                AuditHmacKey             = s.AuditHmacKey,
                PollSigningPrivateKeyPem = s.PollSigningPrivateKeyPem,
            },
            RaizenApi = new
            {
                BaseUrl = $"https://localhost:{s.ApiPort}"
            },
            AgentDeployment = new
            {
                ServerUrl          = s.PublicApiUrl,
                InstallerPath      = s.AgentMsiInstallPath,
                // SHA-256 thumbprint of the TLS cert — read by the Web portal when
                // generating agent deployment packages so agents can pin to it.
                TlsCertThumbprint  = s.CertThumbprintSha256,
                // Set this to the MSI version string (e.g. "1.0.14") after each release.
                // Endpoints will auto-update within 4 hours when this is newer than their
                // installed version.  Leave empty to disable auto-update.
                CurrentVersion     = (string?)null,
            },
            AllowedHosts = "*"
        };
        Write(Path.Combine(s.WebInstallDir, "appsettings.Production.json"), config);
    }

    /// <summary>
    /// Writes the cert PFX to the certs directory and returns the path.
    /// </summary>
    public static void WriteCert(WizardState s, byte[] pfxBytes)
    {
        Directory.CreateDirectory(s.CertDir);
        File.WriteAllBytes(s.CertPfxPath, pfxBytes);

        // Grant SYSTEM and Administrators explicit full control on the certs directory.
        // We do NOT strip inherited ACLs — the services run as LocalSystem which is SYSTEM,
        // and stripping inherited ACLs has caused intermittent UnauthorizedAccessException
        // on service startup (the file exists but the service cannot read it).
        var dirInfo = new DirectoryInfo(s.CertDir);
        var acl     = dirInfo.GetAccessControl();
        var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        var prop  = System.Security.AccessControl.PropagationFlags.None;
        var allow = System.Security.AccessControl.AccessControlType.Allow;
        var full  = System.Security.AccessControl.FileSystemRights.FullControl;
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            "NT AUTHORITY\\SYSTEM", full, inherit, prop, allow));
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            "BUILTIN\\Administrators", full, inherit, prop, allow));
        dirInfo.SetAccessControl(acl);
    }

    private static void Write(string path, object config) =>
        File.WriteAllText(path, JsonSerializer.Serialize(config, _opts));
}
