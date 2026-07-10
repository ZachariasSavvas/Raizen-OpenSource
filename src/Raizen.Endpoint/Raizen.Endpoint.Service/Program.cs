using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Raizen.Endpoint.Service;
using Raizen.Endpoint.Service.Actions;
using Raizen.Endpoint.Shared.Config;
using Serilog;
using Serilog.Events;

// ── Config ────────────────────────────────────────────────────────────────────
// Resolve the config file path using the priority chain:
//   1. --config=<path>  command-line argument
//   2. RAIZEN_CONFIG_PATH environment variable
//   3. %ProgramData%\Raizen\raizen-config.json  (default)
string? configPathArg = args
    .FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
    ?[9..];

var configLoader = new ConfigLoader(configPathArg);
var config = configLoader.Current;

// ── Logging (before host build so service start errors are captured) ──────────
bool isWindowsService = WindowsServiceHelpers.IsWindowsService();

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(
        path: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Raizen", "Logs", "service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30);

if (!isWindowsService)
    logConfig = logConfig.WriteTo.Console();
else
    logConfig = logConfig.WriteTo.EventLog(
        "Raizen Endpoint",
        manageEventSource: true,
        restrictedToMinimumLevel: LogEventLevel.Warning);

Log.Logger = logConfig.CreateLogger();

// ── Config validation (fail fast if essential fields are missing) ─────────────
if (string.IsNullOrWhiteSpace(config.ServerUrl) || !Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out _))
{
    Log.Fatal("FATAL: ServerUrl is missing or invalid in config. Service cannot start.");
    return;
}
if (string.IsNullOrWhiteSpace(config.ApiKey) && string.IsNullOrWhiteSpace(config.RegistrationToken))
{
    Log.Fatal("FATAL: Neither ApiKey nor RegistrationToken is set in config. Service cannot start.");
    return;
}
if (config.PollIntervalSeconds < 5)
{
    Log.Fatal("FATAL: PollIntervalSeconds must be >= 5. Current value: {Value}", config.PollIntervalSeconds);
    return;
}

try
{
    Log.Information("Raizen Endpoint Service starting.");

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(opts => opts.ServiceName = "RaizenEndpoint")
        .UseSerilog()
        .ConfigureServices(services =>
        {
            // ── Config singleton ──────────────────────────────────────────────
            services.AddSingleton(configLoader);

            // ── HTTP client (shared, with config-aware base address) ──────────
            services.AddHttpClient("Raizen")
                .ConfigurePrimaryHttpMessageHandler(() =>
                {
                    var handler = new HttpClientHandler();
                    var thumbprint = configLoader.Current.TlsPinThumbprint;
                    if (!string.IsNullOrEmpty(thumbprint))
                    {
                        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                            cert?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                                ?.Equals(thumbprint, StringComparison.OrdinalIgnoreCase) == true;
                    }
                    return handler;
                });

            // ── Action handlers ───────────────────────────────────────────────
            services.AddSingleton<IActionHandler, InstallMsiHandler>();
            services.AddSingleton<IActionHandler>(sp =>
                new LocalGroupMemberHandler(isAdd: true,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalGroupMemberHandler>>()));
            services.AddSingleton<IActionHandler, RemoveLocalGroupMemberHandler>();
            services.AddSingleton<IActionHandler>(sp =>
                new ServiceControlHandler(Raizen.Shared.Enums.ActionType.StartService,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServiceControlHandler>>()));
            services.AddSingleton<IActionHandler>(sp =>
                new ServiceControlHandler(Raizen.Shared.Enums.ActionType.StopService,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServiceControlHandler>>()));
            services.AddSingleton<IActionHandler>(sp =>
                new ServiceControlHandler(Raizen.Shared.Enums.ActionType.RestartService,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServiceControlHandler>>()));
            services.AddSingleton<IActionHandler, CopyFileHandler>();
            services.AddSingleton<IActionHandler, RegistryHandler>();
            services.AddSingleton<IActionHandler, RunApprovedScriptHandler>();
            services.AddSingleton<IActionHandler, RunAsAdminHandler>();
            services.AddSingleton<IActionHandler, FilePermissionsHandler>();
            services.AddSingleton<IActionHandler, NetworkConfigurationHandler>();
            services.AddSingleton<IActionHandler, EnvironmentVariableHandler>();
            services.AddSingleton<IActionHandler, UninstallMsiHandler>();
            services.AddSingleton<IActionHandler, DeleteFileHandler>();
            services.AddSingleton<IActionHandler, CreateLocalUserHandler>();
            services.AddSingleton<IActionHandler, DisableLocalUserHandler>();
            services.AddSingleton<IActionHandler, AddTrustedCertificateHandler>();
            services.AddSingleton<IActionHandler, FirewallRuleHandler>();
            services.AddSingleton<IActionHandler, CollectEventLogsHandler>();

            services.AddSingleton<ActionHandlerRegistry>();
            services.AddSingleton<OfflineQueueService>();
            services.AddSingleton<AgentUpdateService>();
            services.AddSingleton<AgentHealthState>();
            services.AddSingleton<EndpointHealthCollector>();

            // ── Background workers ────────────────────────────────────────────
            // RegistrationWorker runs first and blocks until the token exchange
            // completes (or fails), ensuring ApiKey is written before polling starts.
            services.AddHostedService<RegistrationWorker>();
            services.AddHostedService<ElevationWorker>();
            services.AddHostedService<HeartbeatWorker>();
            services.AddHostedService<UpdateWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Raizen Endpoint Service terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
