using Raizen.Endpoint.Shared.Config;
using Raizen.Endpoint.Tray;
using Raizen.Endpoint.Tray.Forms;
using Raizen.Endpoint.Tray.Services;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// Resolve config path: --config=<path>  ->  RAIZEN_CONFIG_PATH env  ->  default
string? configPathArg = args
    .FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
    ?[9..];

// ── --run-as-admin=<path>  (right-click "Request to Run as Admin") ─────────────
// Launched directly from the shell context menu on an .exe/.msi/.msc.
// Opens a standalone modern popup form; no tray icon is created.
string? runAsAdminPath = args
    .FirstOrDefault(a => a.StartsWith("--run-as-admin=", StringComparison.OrdinalIgnoreCase))
    ?[15..]?.Trim('"');

if (runAsAdminPath is not null)
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new RunAsAdminForm(client, runAsAdminPath));
    return;
}

// ── --file-transfer=<path>  (right-click "Request File Transfer...") ──────────
string? fileTransferPath = args
    .FirstOrDefault(a => a.StartsWith("--file-transfer=", StringComparison.OrdinalIgnoreCase))
    ?[16..]?.Trim('"');

if (fileTransferPath is not null)
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new FileTransferForm(client, fileTransferPath));
    return;
}

// ── --file-permissions=<path>  (right-click "Request File Permissions...") ────
string? filePermissionsPath = args
    .FirstOrDefault(a => a.StartsWith("--file-permissions=", StringComparison.OrdinalIgnoreCase))
    ?[19..]?.Trim('"');

if (filePermissionsPath is not null)
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new FilePermissionsForm(client, initialPath: filePermissionsPath));
    return;
}

// ── --network-change  (right-click "Request Network Change") ─────────────────
if (args.Any(a => a.Equals("--network-change", StringComparison.OrdinalIgnoreCase)))
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new NetworkChangeForm(client));
    return;
}

// ── --env-var  (tray "Request Environment Variable") ─────────────────────────
if (args.Any(a => a.Equals("--env-var", StringComparison.OrdinalIgnoreCase)))
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new EnvVarForm(client));
    return;
}

// ── --firewall  (right-click "Request Firewall Rule") ────────────────────────
if (args.Any(a => a.Equals("--firewall", StringComparison.OrdinalIgnoreCase)))
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new FirewallForm(client));
    return;
}

// ── --services  (right-click "Services" on any file/folder/desktop) ───────────
if (args.Any(a => a.Equals("--services", StringComparison.OrdinalIgnoreCase)))
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new ServicesForm(client));
    return;
}

// ── --elevate=<path>  (right-click "Request Elevation..." on any file/folder) ──
string? contextPath = args
    .FirstOrDefault(a => a.StartsWith("--elevate=", StringComparison.OrdinalIgnoreCase))
    ?[10..]?.Trim('"');

if (contextPath is not null)
{
    var configLoader = new ConfigLoader(configPathArg);
    var client       = new ServerClient(configLoader);
    Application.Run(new RequestForm(client, contextPath));
    return;
}

// ── Normal tray startup: enforce single instance ───────────────────────────────
using var mutex = new Mutex(initiallyOwned: true, name: "RaizenEndpointTray_SingleInstance", out var isNew);
if (!isNew)
{
    MessageBox.Show("Raizen tray is already running.", "Raizen",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

var loader = new ConfigLoader(configPathArg);
Application.Run(new TrayApplicationContext(loader));
