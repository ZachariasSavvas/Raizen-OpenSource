using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raizen.Endpoint.Shared.Config;

/// <summary>
/// Central configuration for both the Windows Service and the Tray application.
///
/// Default file location:  %ProgramData%\Raizen\raizen-config.json
///
/// To move the file: set the environment variable
///   RAIZEN_CONFIG_PATH=C:\CustomPath\raizen-config.json
/// or pass --config="C:\CustomPath\raizen-config.json" on the command line.
///
/// The file is watched at runtime — changes take effect on the next poll cycle
/// without restarting the service.
/// </summary>
public sealed class RaizenEndpointConfig
{
    // ── Connection ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Base URL of the Raizen API server.
    /// Change this value whenever the server is moved.
    /// Example: "https://raizen.contoso.com:5001"
    /// </summary>
    public string ServerUrl { get; set; } = "https://raizen.contoso.com:5001";

    /// <summary>
    /// Optional SHA-256 thumbprint of the server TLS certificate for certificate pinning.
    /// Leave empty to trust the system CA store.
    /// </summary>
    public string TlsPinThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// RSA public key in PEM format used to verify the server's poll-response signatures.
    /// Copy this value from the server's startup log (look for "Poll-response signing public key").
    /// When set, the endpoint will reject any poll response whose signature does not match,
    /// preventing MITM attacks from injecting fake approvals even over plain HTTP.
    /// Leave empty to disable signature verification (not recommended in production).
    /// </summary>
    public string ServerPublicKeyPem { get; set; } = string.Empty;

    // ── Machine identity ───────────────────────────────────────────────────────
    /// <summary>
    /// Stable unique identifier for this machine.
    /// Auto-generated on first run (motherboard UUID → GUID).
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>
    /// One-time registration token from the agent deployment package.
    /// Present only on first boot before the token has been exchanged for a permanent ApiKey.
    /// Cleared automatically by the service after a successful exchange.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistrationToken { get; set; }

    /// <summary>
    /// Secret API key for this endpoint.
    /// Set automatically after the RegistrationToken is exchanged on first boot,
    /// or generated during manual installation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    // ── Polling ────────────────────────────────────────────────────────────────
    /// <summary>How often (seconds) the service polls the server for approved requests.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>How often (seconds) the service sends a heartbeat to the server.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 300;

    // ── Request submission proxy ───────────────────────────────────────────────
    /// <summary>
    /// Optional HTTP proxy for all calls to the Raizen server.
    /// Example: "http://proxy.contoso.com:8080"
    /// </summary>
    public string? HttpProxy { get; set; }

    /// <summary>Timeout in seconds for individual HTTP calls to the server.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    // ── Script execution ────────────────────────────────────────────────────────
    /// <summary>Maximum execution time in minutes for approved scripts. Default: 5.</summary>
    public int ScriptTimeoutMinutes { get; set; } = 5;
}

/// <summary>
/// Loads, watches, and saves <see cref="RaizenEndpointConfig"/> from disk.
/// Thread-safe for read access; writes are serialized via a lock.
/// </summary>
public sealed class ConfigLoader : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private RaizenEndpointConfig _current;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private Timer? _debounceTimer;

    public event Action<RaizenEndpointConfig>? ConfigChanged;

    public ConfigLoader(string? overridePath = null)
    {
        _path = ResolveConfigPath(overridePath);
        _current = Load();

        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                // Debounce: the file may be written in multiple chunks.
                // Use a timer that resets on each event so rapid writes coalesce.
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ =>
                {
                    try
                    {
                        var reloaded = Load();
                        lock (_lock) _current = reloaded;
                        ConfigChanged?.Invoke(reloaded);
                    }
                    catch
                    {
                        // File may still be locked by writer; next Changed event will retry
                    }
                }, null, 500, Timeout.Infinite);
            };
        }
    }

    public RaizenEndpointConfig Current { get { lock (_lock) return _current; } }

    public void Save(RaizenEndpointConfig config)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(config, JsonOpts));
            _current = config;
        }
    }

    private RaizenEndpointConfig Load()
    {
        if (!File.Exists(_path))
        {
            // Write a default template so the admin can fill it in
            var defaults = new RaizenEndpointConfig
            {
                MachineId = ReadMachineId(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(defaults, JsonOpts));
            return defaults;
        }

        try
        {
            var json    = File.ReadAllText(_path);
            var result  = JsonSerializer.Deserialize<RaizenEndpointConfig>(json, JsonOpts)
                          ?? new RaizenEndpointConfig();

            // Auto-populate MachineId if the distributed config left it blank
            if (string.IsNullOrEmpty(result.MachineId))
                result.MachineId = ReadMachineId();

            return result;
        }
        catch (Exception ex)
        {
            // Log to stderr so the admin can diagnose config parse failures
            // (the service may not have Serilog configured yet at this point)
            Console.Error.WriteLine($"[Raizen] ERROR: Failed to parse config at '{_path}': {ex.Message}. Using defaults.");
            return new RaizenEndpointConfig();
        }
    }

    /// <summary>
    /// Resolution order:
    ///   1. Explicit overridePath argument (command line --config=...)
    ///   2. RAIZEN_CONFIG_PATH environment variable
    ///   3. %ProgramData%\Raizen\raizen-config.json  (default)
    /// </summary>
    public static string ResolveConfigPath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;

        var env = Environment.GetEnvironmentVariable("RAIZEN_CONFIG_PATH");
        if (!string.IsNullOrEmpty(env)) return env;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Raizen",
            "raizen-config.json");
    }

    private static string ReadMachineId()
    {
        // Use the Windows machine GUID from the registry as the stable machine identity
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(guid)) return guid;
        }
        catch { }

        return Guid.NewGuid().ToString();
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
    }
}
