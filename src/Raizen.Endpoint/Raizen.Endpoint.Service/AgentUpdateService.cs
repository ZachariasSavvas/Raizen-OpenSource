using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Checks for a newer agent version on the server and self-updates by downloading
/// the MSI and running msiexec silently.
///
/// Security (v1.1.0+):
///   • The version response is RSA-signed — the server's identity is verified before
///     trusting the version number or MSI hash.
///   • The MSI bytes are SHA-256 hashed in memory and compared against the hash in the
///     signed payload before any bytes are written to disk or msiexec is called.
///   • If ServerPublicKeyPem is not configured the update is refused entirely.
///   • If the server returns no signed payload (pre-v1.1.0 server) the update is skipped
///     until the server is also upgraded.
///
/// Used by <see cref="UpdateWorker"/> on its 4-hour schedule and by
/// <see cref="ElevationWorker"/> when the server signals an immediate update via
/// <c>SignedPollResponseDto.UpdateNow</c>.
/// </summary>
public sealed class AgentUpdateService(
    ConfigLoader configLoader,
    IHttpClientFactory httpFactory,
    AgentHealthState health,
    ILogger<AgentUpdateService> log)
{
    public AgentUpdateService(
        ConfigLoader configLoader,
        IHttpClientFactory httpFactory,
        ILogger<AgentUpdateService> log)
        : this(configLoader, httpFactory, new AgentHealthState(), log)
    {
    }

    private static readonly Version LocalVersion = NormalizeVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Normalizes a 4-component version (e.g. 1.1.0.0) to 3-component (1.1.0).
    /// Assembly versions are always 4-component (Major.Minor.Build.Revision=0),
    /// but Version.TryParse("1.1.0") returns 3-component (Revision=-1).
    /// .NET considers 1.1.0 &lt; 1.1.0.0 because -1 &lt; 0, which causes the
    /// agent to wrongly skip updates when the version strings represent the same release.
    /// </summary>
    private static Version NormalizeVersion(Version v) =>
        v.Revision <= 0 ? new Version(v.Major, v.Minor, v.Build) : v;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task CheckAndUpdateAsync(CancellationToken ct)
    {
        var config = configLoader.Current;
        if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ServerUrl))
        {
            health.UpdateChecked("not-configured", "Server URL or API key is not configured.");
            return;
        }

        using var client = BuildClient(config);
        health.UpdateChecked("checking");

        // ── 1. Fetch signed version response ────────────────────────────────────
        HttpResponseMessage versionResp;
        try
        {
            versionResp = await client.GetAsync("api/v1/agent/version", ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not reach server for update check.");
            health.UpdateChecked("server-unreachable", ex.Message);
            return;
        }

        if (versionResp.StatusCode == HttpStatusCode.NotFound)
        {
            health.UpdateChecked("not-configured", "Auto-update is not configured on the server.");
            return; // auto-update not configured on server
        }

        if (!versionResp.IsSuccessStatusCode)
        {
            log.LogWarning("Update version check returned {Status}.", versionResp.StatusCode);
            health.UpdateChecked("version-check-failed", $"Version check returned {versionResp.StatusCode}.");
            return;
        }

        var body = await versionResp.Content.ReadFromJsonAsync<SignedAgentVersionDto>(JsonOpts, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Version))
        {
            health.UpdateChecked("version-check-failed", "Version response was empty.");
            return;
        }

        // ── 2. Require signed payload — old servers are not supported ────────────
        if (body.Payload is null)
        {
            log.LogWarning(
                "Server does not return signed version responses (pre-v1.1.0 server). " +
                "Auto-update is disabled until the server is upgraded to v1.1.0+.");
            health.UpdateChecked("unsigned-version-response", "Server does not return signed version responses.");
            return;
        }

        // ── 3. Require ServerPublicKeyPem — update is refused without it ─────────
        var publicKeyPem = config.ServerPublicKeyPem;
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            log.LogError(
                "Auto-update refused: ServerPublicKeyPem is not configured in raizen-config.json. " +
                "Copy the public key from the server startup log and add it to this endpoint's config.");
            health.UpdateChecked("missing-public-key", "ServerPublicKeyPem is not configured.");
            return;
        }

        // ── 4. Verify RSA signature over the payload bytes ───────────────────────
        if (!PollSignatureVerifier.VerifyPayload(body.Payload, body.Signature, publicKeyPem, log))
        {
            health.UpdateChecked("invalid-version-signature", "Version response signature verification failed.");
            return;
        }

        // ── 5. Parse version + hash from the verified payload bytes ─────────────
        AgentVersionPayload? payload;
        try
        {
            var payloadBytes = Convert.FromBase64String(body.Payload);
            payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to deserialise verified version payload.");
            health.UpdateChecked("invalid-version-payload", "Failed to deserialize verified version payload.");
            return;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Version))
        {
            health.UpdateChecked("invalid-version-payload", "Verified version payload was empty.");
            return;
        }

        if (!Version.TryParse(payload.Version, out var parsedServerVersion))
        {
            log.LogWarning("Server returned unparseable version string: {Version}", payload.Version);
            health.UpdateChecked("invalid-version", $"Server returned unparseable version: {payload.Version}");
            return;
        }

        var serverVersion = NormalizeVersion(parsedServerVersion);

        if (serverVersion <= LocalVersion)
        {
            log.LogDebug("Agent is up to date (local={Local}, server={Server}).", LocalVersion, serverVersion);
            health.MarkCurrentVersionHealthy();
            health.UpdateChecked("up-to-date");
            return;
        }

        log.LogInformation(
            "Update available: {Local} -> {Server}. Downloading MSI...",
            LocalVersion, serverVersion);

        // ── 6. Require MSI hash — refuse to install without it ──────────────────
        if (string.IsNullOrEmpty(payload.MsiSha256))
        {
            log.LogWarning(
                "Server version response contains no MSI hash. " +
                "Cannot verify update integrity. Update skipped.");
            health.UpdateChecked("missing-msi-hash", "Server version response contains no MSI hash.");
            return;
        }

        var expectedHash = payload.MsiSha256.ToLowerInvariant();

        // ── 7. Download MSI into memory ──────────────────────────────────────────
        byte[] msiBytes;
        try
        {
            using var msiResp = await client.GetAsync("api/v1/agent/msi", ct);
            if (!msiResp.IsSuccessStatusCode)
            {
                log.LogWarning("MSI download returned {Status}.", msiResp.StatusCode);
                health.UpdateChecked("msi-download-failed", $"MSI download returned {msiResp.StatusCode}.");
                return;
            }

            msiBytes = await msiResp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to download MSI.");
            health.UpdateChecked("msi-download-failed", ex.Message);
            return;
        }

        // ── 8. Verify SHA-256 before writing a single byte to disk ───────────────
        var actualHash = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(expectedHash)))
        {
            log.LogCritical(
                "MSI HASH MISMATCH — update aborted. Expected={Expected} Actual={Actual}. " +
                "The downloaded file does not match what the server signed. " +
                "This may indicate tampering or a corrupted download.",
                expectedHash, actualHash);
            health.UpdateChecked("msi-hash-mismatch", "Downloaded MSI hash did not match the signed hash.");
            return;
        }

        log.LogInformation("MSI hash verified (SHA-256 match). Writing to temp path.");

        // ── 9. Write verified bytes to temp file ─────────────────────────────────
        var msiPath = Path.Combine(Path.GetTempPath(), $"RaizenEndpoint-{serverVersion}.msi");
        try
        {
            await File.WriteAllBytesAsync(msiPath, msiBytes, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write MSI to temp path.");
            health.UpdateChecked("msi-write-failed", ex.Message);
            return;
        }

        // ── 10. Launch msiexec (fire-and-forget) ─────────────────────────────────
        // msiexec stops this service, replaces the binaries, and restarts it.
        // We do not wait — the process will stop us before it finishes.
        var logPath = Path.Combine(Path.GetTempPath(), "raizen-update.log");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "msiexec.exe",
                Arguments       = $"/i \"{msiPath}\" /quiet /norestart /log \"{logPath}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };

            Process.Start(psi);
            log.LogInformation("Launched msiexec to apply update to {Version}.", serverVersion);
            health.UpdateLaunched();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to launch msiexec for update.");
            health.UpdateChecked("msiexec-launch-failed", ex.Message);
        }
    }

    private HttpClient BuildClient(RaizenEndpointConfig config)
    {
        var client = httpFactory.CreateClient("Raizen");
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);
        client.DefaultRequestHeaders.Add("X-Raizen-ApiKey", config.ApiKey);
        return client;
    }
}
