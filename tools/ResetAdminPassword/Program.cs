using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

// ── Parse CLI args ───────────────────────────────────────────────────────────
var cliConnStr       = GetArg("--connection-string") ?? GetArg("-c");
var cliEncKey        = GetArg("--encryption-key")    ?? GetArg("-k");
var cliAppsettings   = GetArg("--appsettings")       ?? GetArg("-a");
var cliUsername       = GetArg("--username")          ?? GetArg("-u");
var cliPassword      = GetArg("--password")          ?? GetArg("-p");
var cliForceChange   = args.Contains("--force-change");

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

// ── Resolve connection string & encryption key ───────────────────────────────
string connStr, encryptionKey;

if (cliConnStr != null && cliEncKey != null)
{
    connStr       = cliConnStr;
    encryptionKey = cliEncKey;
}
else
{
    // Fall back to appsettings file
    var settingsPath = cliAppsettings
        ?? FindAppsettings()
        ?? throw new Exception(
            "Could not find appsettings. Use --connection-string + --encryption-key, " +
            "or --appsettings <path>.");

    if (!File.Exists(settingsPath))
    {
        Console.Error.WriteLine($"Settings file not found: {settingsPath}");
        return 1;
    }

    Console.WriteLine($"Using settings: {settingsPath}");
    var config = new ConfigurationBuilder().AddJsonFile(settingsPath).Build();

    encryptionKey = cliEncKey ?? config["Security:EncryptionKey"]
        ?? throw new Exception("Security:EncryptionKey missing. Use --encryption-key or set it in appsettings.");
    connStr = cliConnStr ?? config["ConnectionStrings:Default"]
        ?? throw new Exception("ConnectionStrings:Default missing. Use --connection-string or set it in appsettings.");
}

// ── Prompt for username / password if not provided ───────────────────────────
var username = cliUsername;
if (string.IsNullOrEmpty(username))
{
    Console.Write("Username to reset: ");
    username = Console.ReadLine()?.Trim();
}
if (string.IsNullOrEmpty(username)) { Console.Error.WriteLine("Username is required."); return 1; }

var password = cliPassword;
if (string.IsNullOrEmpty(password))
{
    Console.Write("New password: ");
    password = ReadPassword();
    Console.WriteLine();
    Console.Write("Confirm password: ");
    var confirm = ReadPassword();
    Console.WriteLine();
    if (password != confirm) { Console.Error.WriteLine("Passwords do not match."); return 1; }
}
if (password.Length < 8) { Console.Error.WriteLine("Password must be at least 8 characters."); return 1; }

// ── Hash password (same algorithm as AdminAuthService) ───────────────────────
Console.Write("Hashing...");
var newHash = HashPassword(password, encryptionKey);
Console.WriteLine(" done.");

// ── Update DB ────────────────────────────────────────────────────────────────
await using var conn = new NpgsqlConnection(connStr);
await conn.OpenAsync();

// Verify user exists first
await using var checkCmd = conn.CreateCommand();
checkCmd.CommandText = """SELECT "Username", "IsActive", "Role" FROM admin_users WHERE lower("Username") = lower(@username)""";
checkCmd.Parameters.AddWithValue("username", username);
await using var reader = await checkCmd.ExecuteReaderAsync();
if (!await reader.ReadAsync())
{
    Console.Error.WriteLine($"No user found with username '{username}'.");
    Console.Error.WriteLine();

    // List existing users to help
    await reader.CloseAsync();
    await using var listCmd = conn.CreateCommand();
    listCmd.CommandText = """SELECT "Username", "Role", "IsActive" FROM admin_users ORDER BY "Username" """;
    await using var listReader = await listCmd.ExecuteReaderAsync();
    Console.Error.WriteLine("Existing users:");
    while (await listReader.ReadAsync())
    {
        var active = listReader.GetBoolean(2) ? "" : " [DISABLED]";
        Console.Error.WriteLine($"  {listReader.GetString(0)} ({listReader.GetString(1)}){active}");
    }
    return 1;
}
var currentRole   = reader.GetString(2);
var currentActive = reader.GetBoolean(1);
await reader.CloseAsync();

if (!currentActive)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Warning: user '{username}' is currently disabled. Password will be reset but account remains disabled.");
    Console.ResetColor();
}

var mustChange = cliForceChange;

await using var updateCmd = conn.CreateCommand();
updateCmd.CommandText = """
    UPDATE admin_users
    SET    "PasswordHash" = @hash,
           "MustChangePassword" = @mustChange,
           "PasswordChangedAt" = now()
    WHERE  lower("Username") = lower(@username)
    RETURNING "Username"
    """;
updateCmd.Parameters.AddWithValue("hash", newHash);
updateCmd.Parameters.AddWithValue("username", username);
updateCmd.Parameters.AddWithValue("mustChange", mustChange);

var returned = await updateCmd.ExecuteScalarAsync();

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Password reset successfully for: {returned} (Role: {currentRole})");
if (mustChange) Console.WriteLine("User will be prompted to change password on next login.");
Console.ResetColor();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────
string? GetArg(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

string? FindAppsettings()
{
    // Try common locations relative to CWD
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
    const int Iterations = 100000;
    const int HashBytes  = 32;
    const int SaltBytes  = 16;
    const int NonceBytes = 12;
    const int TagBytes   = 16;

    var appKey = SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey));

    var salt      = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash      = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
    var plaintext = new byte[SaltBytes + HashBytes];
    salt.CopyTo(plaintext, 0);
    hash.CopyTo(plaintext, SaltBytes);

    var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
    var ciphertext = new byte[plaintext.Length];
    var tag        = new byte[TagBytes];

    using var aes = new AesGcm(appKey, TagBytes);
    aes.Encrypt(nonce, plaintext, ciphertext, tag);

    var combined = new byte[NonceBytes + plaintext.Length + TagBytes];
    nonce.CopyTo(combined, 0);
    ciphertext.CopyTo(combined, NonceBytes);
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
        if (k.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
            continue;
        }
        if (k.KeyChar != '\0') { sb.Append(k.KeyChar); Console.Write('*'); }
    }
    return sb.ToString();
}

static void PrintUsage()
{
    Console.WriteLine("""
    Raizen Admin Password Reset Tool

    Resets an admin portal user's password directly in the database.
    Use when locked out of the web portal.

    USAGE:
      ResetAdminPassword [options]

    OPTIONS:
      -c, --connection-string <str>  PostgreSQL connection string
      -k, --encryption-key <key>     Security:EncryptionKey value from appsettings
      -a, --appsettings <path>       Path to appsettings.json (auto-detected if omitted)
      -u, --username <name>          Username to reset (prompted if omitted)
      -p, --password <pwd>           New password (prompted if omitted)
          --force-change             Require password change on next login
      -h, --help                     Show this help

    EXAMPLES:
      # Interactive (auto-detects appsettings from current directory)
      ResetAdminPassword

      # Point to specific appsettings
      ResetAdminPassword -a "C:\RaizenServer\web\appsettings.json"

      # Direct connection (no appsettings needed)
      ResetAdminPassword -c "Host=localhost;Database=raizen;Username=postgres;Password=secret" -k "my-encryption-key"

      # Fully scripted (no prompts)
      ResetAdminPassword -c "Host=localhost;Database=raizen;Username=postgres;Password=secret" -k "my-key" -u Admin -p "NewPass123!"
    """);
}
