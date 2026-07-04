using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

var cliConnStr     = GetArg("--connection-string") ?? GetArg("-c");
var cliAppsettings = GetArg("--appsettings")       ?? GetArg("-a");
var cliOutput      = GetArg("--output")            ?? GetArg("-o");
var cliDays        = GetArg("--days")              ?? GetArg("-d") ?? "30";
var cliEvent       = GetArg("--event");
var cliActor       = GetArg("--actor");
var cliMachine     = GetArg("--machine");

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

if (!int.TryParse(cliDays, out var days) || days < 1 || days > 3650)
{
    Console.Error.WriteLine("--days must be between 1 and 3650.");
    return 1;
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

// ── Query ────────────────────────────────────────────────────────────────────
await using var conn = new NpgsqlConnection(connStr);
await conn.OpenAsync();

var sql = new StringBuilder("""
    SELECT a."Id", a."Event", a."ActorUpn", a."TargetMachine", a."Detail",
           a."OccurredAt", a."IpAddress", a."RequestId"
    FROM audit_logs a
    WHERE a."OccurredAt" >= now() - @interval::interval
    """);

if (cliEvent != null)  sql.Append(""" AND a."Event" ILIKE '%' || @event || '%'""");
if (cliActor != null)  sql.Append(""" AND a."ActorUpn" ILIKE '%' || @actor || '%'""");
if (cliMachine != null) sql.Append(""" AND a."TargetMachine" ILIKE '%' || @machine || '%'""");

sql.Append(""" ORDER BY a."OccurredAt" ASC""");

await using var cmd = conn.CreateCommand();
cmd.CommandText = sql.ToString();
cmd.Parameters.AddWithValue("interval", $"{days} days");
if (cliEvent != null)  cmd.Parameters.AddWithValue("event", cliEvent);
if (cliActor != null)  cmd.Parameters.AddWithValue("actor", cliActor);
if (cliMachine != null) cmd.Parameters.AddWithValue("machine", cliMachine);

await using var reader = await cmd.ExecuteReaderAsync();

// ── Output ───────────────────────────────────────────────────────────────────
var outputPath = cliOutput ?? $"raizen-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
var count = 0;

await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
await writer.WriteLineAsync("Id,OccurredAt,Event,ActorUpn,TargetMachine,IpAddress,RequestId,Detail");

while (await reader.ReadAsync())
{
    var id        = reader.GetGuid(0);
    var evt       = reader.GetString(1);
    var actor     = reader.GetString(2);
    var machine   = reader.IsDBNull(3) ? "" : reader.GetString(3);
    var detail    = reader.IsDBNull(4) ? "" : reader.GetString(4);
    var occurred  = reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss");
    var ip        = reader.IsDBNull(6) ? "" : reader.GetString(6);
    var requestId = reader.IsDBNull(7) ? "" : reader.GetGuid(7).ToString();

    await writer.WriteLineAsync($"{id},{occurred},{CsvEscape(evt)},{CsvEscape(actor)},{CsvEscape(machine)},{ip},{requestId},{CsvEscape(detail)}");
    count++;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Exported {count:N0} audit log entries to: {Path.GetFullPath(outputPath)}");
Console.ResetColor();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────
static string CsvEscape(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
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
    Raizen Audit Log Export Tool

    Exports audit log entries to CSV for compliance reporting or analysis.

    USAGE:
      ExportAuditLog [options]

    OPTIONS:
      -c, --connection-string <str>  PostgreSQL connection string
      -a, --appsettings <path>       Path to appsettings.json (auto-detected if omitted)
      -o, --output <file>            Output CSV file (default: raizen-audit-YYYYMMDD-HHmmss.csv)
      -d, --days <n>                 Export last N days (default: 30)
          --event <filter>           Filter by event type (partial match)
          --actor <filter>           Filter by actor UPN (partial match)
          --machine <filter>         Filter by target machine (partial match)
      -h, --help                     Show this help

    EXAMPLES:
      # Export last 30 days
      ExportAuditLog

      # Export last 90 days to specific file
      ExportAuditLog -d 90 -o "audit-q1-2026.csv"

      # Export only login events
      ExportAuditLog --event "auth.login"

      # Export actions on a specific machine
      ExportAuditLog --machine "WORKSTATION01" -d 60

      # Direct connection
      ExportAuditLog -c "Host=localhost;Database=raizen;Username=postgres;Password=secret" -d 30
    """);
}
