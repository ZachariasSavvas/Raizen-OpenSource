using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Raizen.Server.Api.Services;

/// <summary>
/// Thread-safe singleton that caches the SHA-256 hash of the MSI installer.
/// Recomputes automatically when the file's last-write timestamp changes,
/// so a newly deployed MSI is picked up without a server restart.
/// </summary>
public sealed class MsiHashCache(IConfiguration config, ILogger<MsiHashCache> log)
{
    private readonly object _lock           = new();
    private string?         _cachedHash;
    private DateTime        _cachedWriteTime = DateTime.MinValue;
    private string?         _cachedPath;

    /// <summary>
    /// Returns the lowercase hex SHA-256 of the configured MSI, or null if
    /// no installer path is configured, the file does not exist, or hashing fails.
    /// </summary>
    public string? GetHash()
    {
        var path = config["AgentDeployment:InstallerPath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        lock (_lock)
        {
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(path);

                if (_cachedHash is not null
                    && path == _cachedPath
                    && writeTime == _cachedWriteTime)
                    return _cachedHash;

                log.LogInformation("Computing SHA-256 hash for MSI at {Path}.", path);
                using var stream = File.OpenRead(path);
                using var sha    = SHA256.Create();
                _cachedHash      = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                _cachedWriteTime = writeTime;
                _cachedPath      = path;
                log.LogInformation("MSI hash cached: {Hash}", _cachedHash);
                return _cachedHash;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to compute MSI hash for {Path}.", path);
                return null;
            }
        }
    }
}
