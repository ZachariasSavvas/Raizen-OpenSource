using Microsoft.Extensions.Configuration;
using Npgsql;

var cliConnStr     = GetArg("--connection-string") ?? GetArg("-c");
var cliAppsettings = GetArg("--appsettings")       ?? GetArg("-a");

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

// ── Connect ──────────────────────────────────────────────────────────────────
Console.WriteLine("Connecting to database...");
await using var conn = new NpgsqlConnection(connStr);
try
{
    await conn.OpenAsync();
    Ok("Database connection successful");
}
catch (Exception ex)
{
    Fail($"Database connection failed: {ex.Message}");
    return 1;
}

// ── PostgreSQL version ───────────────────────────────────────────────────────
var version = await Scalar("SELECT version()");
Console.WriteLine($"  PostgreSQL: {version}");
Console.WriteLine();

// ── Check required tables ────────────────────────────────────────────────────
Console.WriteLine("Checking tables...");
string[] requiredTables =
[
    "admin_users", "elevation_requests", "action_definitions",
    "endpoint_registrations", "audit_logs", "notification_settings",
    "registration_tokens", "request_approvals", "request_comments",
    "password_history", "login_lockouts", "server_settings",
    "__EFMigrationsHistory"
];

foreach (var table in requiredTables)
{
    var exists = await Scalar(
        "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = @t)",
        ("t", table));
    if (exists is true)
        Ok($"  {table}");
    else
        Warn($"  {table} (MISSING)");
}
Console.WriteLine();

// ── Table counts ─────────────────────────────────────────────────────────────
Console.WriteLine("Record counts:");
(string table, string label)[] countTables =
[
    ("admin_users", "Admin users"),
    ("endpoint_registrations", "Registered endpoints"),
    ("action_definitions", "Action definitions"),
    ("elevation_requests", "Elevation requests"),
    ("audit_logs", "Audit log entries"),
    ("registration_tokens", "Registration tokens"),
    ("notification_settings", "Notification configs"),
];

foreach (var (table, label) in countTables)
{
    try
    {
        var count = await Scalar($"SELECT COUNT(*) FROM {table}");
        Console.WriteLine($"  {label,-25} {count}");
    }
    catch
    {
        Warn($"  {label,-25} (table missing or inaccessible)");
    }
}
Console.WriteLine();

// ── Admin users ──────────────────────────────────────────────────────────────
Console.WriteLine("Admin users:");
try
{
    await using var userCmd = conn.CreateCommand();
    userCmd.CommandText = """
        SELECT "Username", "Role", "IsActive", "TotpEnabled", "LastLoginAt", "MustChangePassword"
        FROM admin_users ORDER BY "Username"
        """;
    await using var reader = await userCmd.ExecuteReaderAsync();
    Console.WriteLine($"  {"Username",-20} {"Role",-12} {"Active",-8} {"MFA",-6} {"Must Chg",-10} {"Last Login"}");
    Console.WriteLine($"  {"────────────────",-20} {"────────",-12} {"──────",-8} {"────",-6} {"────────",-10} {"───────────────────"}");
    while (await reader.ReadAsync())
    {
        var uname     = reader.GetString(0);
        var role      = reader.GetString(1);
        var active    = reader.GetBoolean(2) ? "Yes" : "NO";
        var totp      = reader.GetBoolean(3) ? "Yes" : "No";
        var mustChg   = reader.GetBoolean(5) ? "Yes" : "No";
        var lastLogin = reader.IsDBNull(4) ? "Never" : reader.GetDateTime(4).ToString("yyyy-MM-dd HH:mm");
        Console.WriteLine($"  {uname,-20} {role,-12} {active,-8} {totp,-6} {mustChg,-10} {lastLogin}");
    }
    await reader.CloseAsync();
}
catch (Exception ex)
{
    Warn($"  Could not read admin users: {ex.Message}");
}
Console.WriteLine();

// ── Endpoints ────────────────────────────────────────────────────────────────
Console.WriteLine("Endpoints (top 20):");
try
{
    await using var epCmd = conn.CreateCommand();
    epCmd.CommandText = """
        SELECT "MachineName", "AgentVersion", "IsEnabled", "LastSeenAt"
        FROM endpoint_registrations
        ORDER BY "LastSeenAt" DESC NULLS LAST
        LIMIT 20
        """;
    await using var epReader = await epCmd.ExecuteReaderAsync();
    Console.WriteLine($"  {"Machine",-25} {"Version",-12} {"Active",-8} {"Last Seen"}");
    Console.WriteLine($"  {"────────────────────",-25} {"────────",-12} {"──────",-8} {"───────────────────"}");
    while (await epReader.ReadAsync())
    {
        var name    = epReader.GetString(0);
        var ver     = epReader.IsDBNull(1) ? "?" : epReader.GetString(1);
        var active  = epReader.GetBoolean(2) ? "Yes" : "No";
        var lastSeen = epReader.IsDBNull(3) ? "Never" : epReader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm");
        Console.WriteLine($"  {name,-25} {ver,-12} {active,-8} {lastSeen}");
    }
    await epReader.CloseAsync();
}
catch (Exception ex)
{
    Warn($"  Could not read endpoints: {ex.Message}");
}
Console.WriteLine();

// ── Request stats ────────────────────────────────────────────────────────────
Console.WriteLine("Request stats (last 30 days):");
try
{
    var pending   = await Scalar("""SELECT COUNT(*) FROM elevation_requests WHERE "Status" = 0""");
    var approved  = await Scalar("""SELECT COUNT(*) FROM elevation_requests WHERE "Status" = 1""");
    var succeeded = await Scalar("""SELECT COUNT(*) FROM elevation_requests WHERE "Status" = 3 AND "ExecutedAt" > now() - interval '30 days'""");
    var failed    = await Scalar("""SELECT COUNT(*) FROM elevation_requests WHERE "Status" = 4 AND "ExecutedAt" > now() - interval '30 days'""");
    Console.WriteLine($"  Pending:    {pending}");
    Console.WriteLine($"  Approved:   {approved}");
    Console.WriteLine($"  Succeeded:  {succeeded}");
    Console.WriteLine($"  Failed:     {failed}");
}
catch (Exception ex)
{
    Warn($"  Could not read request stats: {ex.Message}");
}
Console.WriteLine();

// ── DB size ──────────────────────────────────────────────────────────────────
try
{
    var dbName = await Scalar("SELECT current_database()");
    var dbSize = await Scalar($"SELECT pg_size_pretty(pg_database_size(current_database()))");
    Console.WriteLine($"Database size: {dbSize} ({dbName})");
}
catch { }

Console.WriteLine();
Ok("Diagnostics complete.");
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────
async Task<object?> Scalar(string sql, params (string name, object value)[] parameters)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters)
        cmd.Parameters.AddWithValue(name, value);
    return await cmd.ExecuteScalarAsync();
}

void Ok(string msg)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("[OK] ");
    Console.ResetColor();
    Console.WriteLine(msg);
}

void Warn(string msg)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("[!!] ");
    Console.ResetColor();
    Console.WriteLine(msg);
}

void Fail(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("[FAIL] ");
    Console.ResetColor();
    Console.WriteLine(msg);
}

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
    Raizen Database Diagnostics Tool

    Checks database connectivity, verifies table structure, and displays
    system health information. Does not modify any data.

    USAGE:
      DiagnoseDb [options]

    OPTIONS:
      -c, --connection-string <str>  PostgreSQL connection string
      -a, --appsettings <path>       Path to appsettings.json (auto-detected if omitted)
      -h, --help                     Show this help

    EXAMPLES:
      # Auto-detect from current directory (run from server install folder)
      DiagnoseDb

      # Point to specific appsettings
      DiagnoseDb -a "C:\RaizenServer\web\appsettings.json"

      # Direct connection
      DiagnoseDb -c "Host=localhost;Database=raizen;Username=postgres;Password=secret"
    """);
}
