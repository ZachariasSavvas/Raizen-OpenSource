using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IPollResponseSigner
{
    /// <summary>The server's RSA public key in PEM format. Log this so admins can add it to endpoint configs.</summary>
    string PublicKeyPem { get; }

    /// <summary>Signs <paramref name="requests"/> and returns a tamper-evident response.</summary>
    SignedPollResponseDto Sign(List<ElevationRequestDto> requests);

    /// <summary>
    /// Signs a version response so agents can verify the server's identity and the
    /// MSI hash before installing an update.
    /// </summary>
    SignedAgentVersionDto SignVersion(string version, string? msiSha256);
}

/// <summary>
/// Singleton that RSA-signs poll responses so endpoints can detect MITM-injected approvals.
///
/// Key resolution order:
///   1. <c>Security:PollSigningPrivateKeyPem</c> in appsettings (recommended for production)
///   2. <c>%ProgramData%\Raizen\poll-signing-private.pem</c> on disk
///   3. Auto-generate a new RSA-2048 key pair and persist it to path #2
///
/// On startup the public key PEM is always logged at Information level so admins can copy it
/// into endpoint <c>raizen-config.json</c> as <c>ServerPublicKeyPem</c>.
/// </summary>
public sealed class PollResponseSigner : IPollResponseSigner, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly string AutoKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Raizen", "poll-signing-private.pem");

    private readonly RSA _rsa;

    public string PublicKeyPem { get; }

    public PollResponseSigner(IConfiguration config, ILogger<PollResponseSigner> logger)
    {
        _rsa = RSA.Create(2048);

        var configured = config["Security:PollSigningPrivateKeyPem"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            _rsa.ImportFromPem(configured);
            logger.LogInformation("Poll-response signing: RSA private key loaded from configuration.");
        }
        else if (File.Exists(AutoKeyPath))
        {
            _rsa.ImportFromPem(File.ReadAllText(AutoKeyPath));
            logger.LogInformation("Poll-response signing: RSA private key loaded from {Path}.", AutoKeyPath);
        }
        else
        {
            var pem = _rsa.ExportRSAPrivateKeyPem();
            Directory.CreateDirectory(Path.GetDirectoryName(AutoKeyPath)!);
            File.WriteAllText(AutoKeyPath, pem);
            logger.LogWarning(
                "Poll-response signing: no key configured. Generated a new RSA-2048 key and saved to {Path}. " +
                "To use a stable key add Security:PollSigningPrivateKeyPem to appsettings.", AutoKeyPath);
        }

        PublicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();

        logger.LogInformation(
            "Poll-response signing public key (add to endpoint raizen-config.json as ServerPublicKeyPem):\n{Key}",
            PublicKeyPem);
    }

    public SignedPollResponseDto Sign(List<ElevationRequestDto> requests)
    {
        var json         = JsonSerializer.Serialize(requests, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(json);
        var sigBytes     = _rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new SignedPollResponseDto
        {
            Payload   = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(sigBytes),
            Requests  = requests,
        };
    }

    public SignedAgentVersionDto SignVersion(string version, string? msiSha256)
    {
        var payload = new AgentVersionPayload { Version = version, MsiSha256 = msiSha256 };
        var json         = JsonSerializer.Serialize(payload, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(json);
        var sigBytes     = _rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new SignedAgentVersionDto
        {
            Version   = version,                              // top-level for old agents
            Payload   = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(sigBytes),
        };
    }

    public void Dispose() => _rsa.Dispose();
}
