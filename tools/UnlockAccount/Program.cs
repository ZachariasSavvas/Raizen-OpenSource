using Microsoft.Extensions.Configuration;
using Npgsql;

var cliConnStr     = GetArg("--connection-string") ?? GetArg("-c");
var cliAppsettings = GetArg("--appsettings")       ?? GetArg("-a");
var cliIp          = GetArg("--ip");
var cliUsername    = GetArg("--username") ?? GetArg("-u");
var clearAll       = args.Contains("--all");
var listOnly       = args.Contains("--list");

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

// ── Resolve connection string ────────────────────────────────────────────────
var connStr = cliConnStr;
if (connStr == null)
{
    var path = cliAppsettings ?? FindAppsettings()
        ?? throw new Exception("Could not find appsettings. Use --connection-string or --appsettings <path>.");

    if (!File.Exists(path)) throw new FileNotFoundException($"Settings file not found: {path}");
    Console.WriteLine($"Using settings: {path}");

    var config = new ConfigurationBuilder().AddJsonFile(path).Build();
    connStr = config["ConnectionStrings:Default"]
        ?? throw new Exception("ConnectionStrings:Default missing.");
}

await using var conn = new NpgsqlConnection(connStr);
await conn.OpenAsync();

// ── List current lockouts ────────────────────────────────────────────────────
await using var listCmd = conn.CreateCommand();
listCmd.CommandText = """
    SELECT ip, count, locked_until
    FROM login_lockouts
    WHERE locked_until > now()
    ORDER BY locked_until DESC
    """;
await using var reader = await listCmd.ExecuteReaderAsync();

var lockouts = new List<(string Ip, int Count, DateTimeOffset Until)>();
while (await reader.ReadAsync())
    lockouts.Add((reader.GetString(0), reader.GetInt32(1), reader.GetDateTime(2)));
await reader.CloseAsync();

if (lockouts.Count == 0)
{
    Console.WriteLine("No active lockouts found.");
    if (listOnly) return 0;
}
else
{
    Console.WriteLine($"\nActive lockouts ({lockouts.Count}):");
    Console.WriteLine($"  {"IP Address",-20} {"Attempts",-10} {"Locked Until"}");
    Console.WriteLine($"  {"─────────────────",-20} {"────────",-10} {"───────────────────────"}");
    foreach (var (ip, count, until) in lockouts)
        Console.WriteLine($"  {ip,-20} {count,-10} {until:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine();
}

if (listOnly) return 0;

// ── Unlock by username (re-enable disabled account) ──────────────────────────
if (cliUsername != null)
{
    await using var enableCmd = conn.CreateCommand();
    enableCmd.CommandText = """
        UPDATE admin_users SET "IsActive" = true
        WHERE lower("Username") = lower(@u) AND "IsActive" = false
        RETURNING "Username"
        """;
    enableCmd.Parameters.AddWithValue("u", cliUsername);
    var enabled = await enableCmd.ExecuteScalarAsync();
    if (enabled != null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Re-enabled disabled account: {enabled}");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine($"User '{cliUsername}' is already active or does not exist.");
    }
}

// ── Clear lockouts ───────────────────────────────────────────────────────────
if (clearAll)
{
    await using var clearCmd = conn.CreateCommand();
    clearCmd.CommandText = "DELETE FROM login_lockouts";
    var deleted = await clearCmd.ExecuteNonQueryAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Cleared all lockouts ({deleted} entries removed).");
    Console.ResetColor();
    Console.WriteLine("Note: the server caches lockouts in memory. Restart RaizenWeb service for immediate effect.");
}
else if (cliIp != null)
{
    await using var clearCmd = conn.CreateCommand();
    clearCmd.CommandText = "DELETE FROM login_lockouts WHERE ip = @ip";
    clearCmd.Parameters.AddWithValue("ip", cliIp);
    var deleted = await clearCmd.ExecuteNonQueryAsync();

    if (deleted > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Cleared lockout for IP: {cliIp}");
        Console.ResetColor();
        Console.WriteLine("Note: the server caches lockouts in memory. Restart RaizenWeb service for immediate effect.");
    }
    else
    {
        Console.WriteLine($"No lockout found for IP: {cliIp}");
    }
}
else if (lockouts.Count > 0 && cliUsername == null)
{
    Console.WriteLine("Use --ip <address> to unlock a specific IP, or --all to clear all lockouts.");
}

return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────
string? GetArg(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string? FindAppsettings()
{
    string[] candidates =
    [
        Path.Combine(Environment.CurrentDirectory, "appsettings.json"),
        Path.Combine(Environment.CurrentDirectory, "appsettings.Production.json"),
        Path.Combine(Environment.CurrentDirectory, "web", "appsettings.json"),
        Path.Combine(Environment.CurrentDirectory, "web", "appsettings.Production.json"),
        Path.Combine(Environment.CurrentDirectory, @"src\Raizen.Server\Raizen.Server.Web\appsettings.Development.json"),
    ];
    return candidates.FirstOrDefault(File.Exists);
}

static void PrintUsage()
{
    Console.WriteLine("""
    Raizen Unlock Account Tool

    Lists and clears login lockouts, and re-enables disabled admin accounts.

    USAGE:
      UnlockAccount [options]

    OPTIONS:
      -c, --connection-string <str>  PostgreSQL connection string
      -a, --appsettings <path>       Path to appsettings.json (auto-detected if omitted)
          --list                     Show active lockouts without making changes
          --ip <address>             Clear lockout for a specific IP address
          --all                      Clear all lockouts
      -u, --username <name>          Re-enable a disabled admin account
      -h, --help                     Show this help

    EXAMPLES:
      # List current lockouts
      UnlockAccount --list

      # Unlock a specific IP
      UnlockAccount --ip 192.168.1.50

      # Clear all lockouts
      UnlockAccount --all

      # Re-enable a disabled user and clear all lockouts
      UnlockAccount -u Admin --all
    """);
}
