using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raizen.Server.Core.Data;

namespace Raizen.Server.Core.Services;

public sealed class SyslogOptions
{
    public const string Section = "Syslog";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 514;

    /// <summary>"Udp" (default) or "Tcp".</summary>
    public string Protocol { get; set; } = "Udp";

    /// <summary>"CEF" (default) or "RFC5424".</summary>
    public string Format { get; set; } = "CEF";

    /// <summary>
    /// Syslog facility number. 13 = log_audit (recommended for security events),
    /// 16-23 = local0-local7.
    /// </summary>
    public int Facility { get; set; } = 13;

    public string AppName { get; set; } = "Raizen";
}

public sealed record SyslogPayload(
    string EventName,
    string ActorUpn,
    string? TargetMachine,
    string? Detail,
    string? IpAddress,
    DateTimeOffset OccurredAt);

public interface ISyslogSender
{
    /// <summary>Fire-and-forget — never throws; logs warnings on failure.</summary>
    void Send(SyslogPayload payload);

    /// <summary>Returns the current effective options (merged: DB overrides appsettings).</summary>
    SyslogOptions GetOptions();

    /// <summary>Applies new options at runtime and persists them to the database.</summary>
    Task SaveOptionsAsync(SyslogOptions options);

    /// <summary>Sends a test syslog message. Returns null on success, error message on failure.</summary>
    Task<string?> TestAsync(SyslogOptions options);

    /// <summary>Loads persisted options from the database (called once at startup).</summary>
    Task LoadFromDatabaseAsync();
}

/// <summary>
/// Forwards audit events to an on-premise SIEM via UDP or TCP syslog.
/// Supports CEF (ArcSight/Splunk) and RFC 5424 formats.
/// Settings are loaded from appsettings.json at startup, then overridden by
/// database-persisted values (editable via the admin UI).
/// </summary>
public sealed class SyslogSender : ISyslogSender
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyslogSender> _logger;
    private readonly string _hostname = Environment.MachineName;
    private volatile SyslogOptions _opts;

    private const string DbKey = "syslog_options";

    public SyslogSender(
        IOptions<SyslogOptions> opts,
        IServiceScopeFactory scopeFactory,
        ILogger<SyslogSender> logger)
    {
        _opts = opts.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public SyslogOptions GetOptions() => _opts;

    public async Task LoadFromDatabaseAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
            var rows = await db.Database
                .SqlQuery<string>($"SELECT value FROM server_settings WHERE key = {DbKey}")
                .ToListAsync();
            var json = rows.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(json))
            {
                var dbOpts = JsonSerializer.Deserialize<SyslogOptions>(json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (dbOpts is not null)
                    _opts = dbOpts;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load syslog settings from database; using appsettings.json values.");
        }
    }

    public async Task SaveOptionsAsync(SyslogOptions options)
    {
        _opts = options;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO server_settings (key, value, updated_at) VALUES ({0}, {1}, NOW()) " +
            "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = NOW()",
            DbKey, json);
    }

    public async Task<string?> TestAsync(SyslogOptions options)
    {
        var testPayload = new SyslogPayload(
            "syslog.test",
            "admin",
            _hostname,
            "Raizen syslog connectivity test",
            "127.0.0.1",
            DateTimeOffset.UtcNow);

        try
        {
            var msg = options.Format.Equals("RFC5424", StringComparison.OrdinalIgnoreCase)
                ? FormatRfc5424(testPayload, options)
                : FormatCef(testPayload, options);
            var data = Encoding.UTF8.GetBytes(msg);

            if (options.Protocol.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new TcpClient();
                client.SendTimeout = 5000;
                await client.ConnectAsync(options.Host, options.Port);
                await using var stream = client.GetStream();
                await stream.WriteAsync(data);
            }
            else
            {
                using var udp = new UdpClient();
                await udp.SendAsync(data, data.Length, options.Host, options.Port);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Send(SyslogPayload payload)
    {
        var opts = _opts;
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.Host)) return;
        _ = Task.Run(() => SendAsync(payload, opts));
    }

    private async Task SendAsync(SyslogPayload p, SyslogOptions opts)
    {
        try
        {
            var msg  = opts.Format.Equals("RFC5424", StringComparison.OrdinalIgnoreCase)
                     ? FormatRfc5424(p, opts) : FormatCef(p, opts);
            var data = Encoding.UTF8.GetBytes(msg);

            if (opts.Protocol.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new TcpClient();
                await client.ConnectAsync(opts.Host, opts.Port);
                await using var stream = client.GetStream();
                await stream.WriteAsync(data);
            }
            else
            {
                using var udp = new UdpClient();
                await udp.SendAsync(data, data.Length, opts.Host, opts.Port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Syslog send failed ({Host}:{Port}). Audit event already stored in DB.",
                opts.Host, opts.Port);
        }
    }

    private string FormatCef(SyslogPayload p, SyslogOptions opts)
    {
        var severity = SyslogSeverity(p.EventName);
        var priority = opts.Facility * 8 + severity;
        var ts       = p.OccurredAt.ToString("MMM dd HH:mm:ss");

        var ext = new StringBuilder();
        ext.Append($"act={Esc(p.EventName)} suser={Esc(p.ActorUpn)}");
        if (p.TargetMachine is not null) ext.Append($" dhost={Esc(p.TargetMachine)}");
        if (p.IpAddress is not null)     ext.Append($" src={p.IpAddress}");
        if (p.Detail is not null)        ext.Append($" msg={Esc(p.Detail)}");

        var cef = $"CEF:0|Raizen|RaizenServer|1.0|{p.EventName}|{p.EventName}|{severity}|{ext}";
        return $"<{priority}>{ts} {_hostname} {cef}\n";
    }

    private string FormatRfc5424(SyslogPayload p, SyslogOptions opts)
    {
        var severity = SyslogSeverity(p.EventName);
        var priority = opts.Facility * 8 + severity;
        var ts       = p.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var msg = new StringBuilder($"event={p.EventName} actor={p.ActorUpn}");
        if (p.TargetMachine is not null) msg.Append($" machine={p.TargetMachine}");
        if (p.IpAddress is not null)     msg.Append($" ip={p.IpAddress}");
        if (p.Detail is not null)        msg.Append($" detail={p.Detail}");

        return $"<{priority}>1 {ts} {_hostname} {opts.AppName} - - - {msg}\n";
    }

    private static int SyslogSeverity(string eventName) =>
        eventName.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        eventName.Contains("denied", StringComparison.OrdinalIgnoreCase)  ? 4  // Warning
      : eventName.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
        eventName.Contains("disabled", StringComparison.OrdinalIgnoreCase) ? 5 // Notice
      : 6; // Informational

    private static string Esc(string s) =>
        s.Replace("|", "\\|").Replace("=", "\\=").Replace("\n", " ").Replace("\r", "");
}

/// <summary>No-op sender used when syslog is disabled (saves service registration branching).</summary>
public sealed class NullSyslogSender : ISyslogSender
{
    public void Send(SyslogPayload payload) { }
    public SyslogOptions GetOptions() => new();
    public Task SaveOptionsAsync(SyslogOptions options) => Task.CompletedTask;
    public Task<string?> TestAsync(SyslogOptions options) => Task.FromResult<string?>("Syslog is not configured.");
    public Task LoadFromDatabaseAsync() => Task.CompletedTask;
}
