using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

var cliConnStr     = GetArg("--connection-string") ?? GetArg("-c");
var cliEncKey      = GetArg("--encryption-key")    ?? GetArg("-k");
var cliAppsettings = GetArg("--appsettings")       ?? GetArg("-a");
var cliUsername    = GetArg("--username")           ?? GetArg("-u");
var cliPassword   = GetArg("--password")           ?? GetArg("-p");
var cliRole       = GetArg("--role")               ?? GetArg("-r") ?? "Admin";

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

// ── Resolve connection string & encryption key ───────────────────────────────
var (connStr, encryptionKey) = ResolveConfig(cliConnStr, cliEncKey, cliAppsettings);

// ── Prompt for username / password ───────────────────────────────────────────
var username = cliUsername;
if (string.IsNullOrEmpty(username))
{
    Console.Write("New username: ");
    username = Console.ReadLine()?.Trim();
}
if (string.IsNullOrEmpty(username)) { Console.Error.WriteLine("Username is required."); return 1; }

// Validate role
string[] validRoles = ["Admin", "Approver", "Operator", "Auditor"];
if (!validRoles.Contains(cliRole, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Invalid role '{cliRole}'. Valid roles: {string.Join(", ", validRoles)}");
    return 1;
}

var password = cliPassword;
if (string.IsNullOrEmpty(password))
{
    Console.Write("Password: ");
    password = ReadPassword();
    Console.WriteLine();
    Console.Write("Confirm password: ");
    var confirm = ReadPassword();
    Console.WriteLine();
    if (password != confirm) { Console.Error.WriteLine("Passwords do not match."); return 1; }
}
if (password.Length < 8) { Console.Error.WriteLine("Password must be at least 8 characters."); return 1; }

// ── Hash & insert ────────────────────────────────────────────────────────────
Console.Write("Hashing...");
var hash = HashPassword(password, encryptionKey);
Console.WriteLine(" done.");

await using var conn = new NpgsqlConnection(connStr);
await conn.OpenAsync();

// Check for duplicate
await using var checkCmd = conn.CreateCommand();
checkCmd.CommandText = """SELECT COUNT(*) FROM admin_users WHERE lower("Username") = lower(@u)""";
checkCmd.Parameters.AddWithValue("u", username);
var exists = (long)(await checkCmd.ExecuteScalarAsync())! > 0;
if (exists)
{
    Console.Error.WriteLine($"User '{username}' already exists. Use ResetAdminPassword to change their password.");
    return 1;
}

await using var cmd = conn.CreateCommand();
cmd.CommandText = """
    INSERT INTO admin_users ("Id", "Username", "PasswordHash", "MustChangePassword", "IsActive", "CreatedAt", "Role")
    VALUES (@id, @username, @hash, true, true, now(), @role)
    RETURNING "Username"
    """;
cmd.Parameters.AddWithValue("id", Guid.NewGuid());
cmd.Parameters.AddWithValue("username", username);
cmd.Parameters.AddWithValue("hash", hash);
cmd.Parameters.AddWithValue("role", cliRole);

var created = await cmd.ExecuteScalarAsync();

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Admin user created: {created} (Role: {cliRole})");
Console.WriteLine("User will be prompted to change password on first login.");
Console.ResetColor();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────
string? GetArg(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

(string ConnStr, string EncKey) ResolveConfig(string? conn, string? enc, string? appsettings)
{
    if (conn != null && enc != null) return (conn, enc);

    var path = appsettings ?? FindAppsettings()
        ?? throw new Exception("Could not find appsettings. Use --connection-string + --encryption-key, or --appsettings <path>.");

    if (!File.Exists(path)) throw new FileNotFoundException($"Settings file not found: {path}");
    Console.WriteLine($"Using settings: {path}");

    var config = new ConfigurationBuilder().AddJsonFile(path).Build();
    return (
        conn ?? config["ConnectionStrings:Default"] ?? throw new Exception("ConnectionStrings:Default missing."),
        enc  ?? config["Security:EncryptionKey"]     ?? throw new Exception("Security:EncryptionKey missing."));
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

static string HashPassword(string password, string encryptionKey)
{
    const int Iterations = 100000, HashBytes = 32, SaltBytes = 16, NonceBytes = 12, TagBytes = 16;
    var appKey    = SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey));
    var salt      = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash      = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
    var plaintext = new byte[SaltBytes + HashBytes];
    salt.CopyTo(plaintext, 0);
    hash.CopyTo(plaintext, SaltBytes);
    var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
    var ct    = new byte[plaintext.Length];
    var tag   = new byte[TagBytes];
    using var aes = new AesGcm(appKey, TagBytes);
    aes.Encrypt(nonce, plaintext, ct, tag);
    var combined = new byte[NonceBytes + plaintext.Length + TagBytes];
    nonce.CopyTo(combined, 0);
    ct.CopyTo(combined, NonceBytes);
    tag.CopyTo(combined, NonceBytes + plaintext.Length);
    return "V1:" + Convert.ToBase64String(combined);
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); continue; }
        if (k.KeyChar != '\0') { sb.Append(k.KeyChar); Console.Write('*'); }
    }
    return sb.ToString();
}

static void PrintUsage()
{
    Console.WriteLine("""
    Raizen Create Admin User Tool

    Creates a new admin portal user directly in the database.
    Use when you need an additional admin account or the default was deleted.

    USAGE:
      CreateAdminUser [options]

    OPTIONS:
      -c, --connection-string <str>  PostgreSQL connection string
      -k, --encryption-key <key>     Security:EncryptionKey value from appsettings
      -a, --appsettings <path>       Path to appsettings.json (auto-detected if omitted)
      -u, --username <name>          Username (prompted if omitted)
      -p, --password <pwd>           Password (prompted if omitted)
      -r, --role <role>              Role: Admin, Approver, Operator, Auditor (default: Admin)
      -h, --help                     Show this help

    EXAMPLES:
      # Interactive
      CreateAdminUser

      # Create an Approver account
      CreateAdminUser -u "john.doe" -r Approver

      # Fully scripted
      CreateAdminUser -c "Host=localhost;Database=raizen;Username=postgres;Password=secret" -k "my-key" -u NewAdmin -p "Pass123!" -r Admin
    """);
}
