using Microsoft.Win32;
using System.Diagnostics;
using System.ServiceProcess;

namespace Raizen.Server.Setup.Installer;

public static class ServiceInstaller
{
    public const string ApiServiceName = "RaizenApi";
    public const string WebServiceName = "RaizenWeb";

    public static void Install(WizardState s)
    {
        var apiExe = Path.Combine(s.ApiInstallDir, "Raizen.Server.Api.exe");
        var webExe = Path.Combine(s.WebInstallDir, "Raizen.Server.Web.exe");

        InstallService(ApiServiceName, "Raizen Elevation API",
            "Raizen brokered JIT elevation API service.", apiExe,
            s.ApiPort);

        InstallService(WebServiceName, "Raizen Admin Portal",
            "Raizen admin web portal.", webExe,
            s.WebPort);
    }

    private static void InstallService(string name, string displayName, string description,
        string binPath, int port)
    {
        // Remove if already registered so we can update config cleanly
        if (ServiceExists(name))
            RunSc($"delete {name}");

        // Create service
        RunSc($"create {name} binPath= \"{binPath}\" start= auto DisplayName= \"{displayName}\"");
        RunSc($"description {name} \"{description}\"");

        // Set environment variables in service registry.
        // ASPNETCORE_URLS is not set here — Kestrel HTTPS endpoint is configured
        // via appsettings.Production.json (Kestrel:Endpoints section) which also
        // specifies the PFX certificate. Only set ASPNETCORE_ENVIRONMENT.
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true)!;
        key.SetValue("Environment",
            new[] { "ASPNETCORE_ENVIRONMENT=Production" },
            RegistryValueKind.MultiString);
    }

    public static void StartServices()
    {
        StartAndWait(ApiServiceName);
        StartAndWait(WebServiceName);
    }

    private static void StartAndWait(string name)
    {
        using var svc = new ServiceController(name);
        if (svc.Status != ServiceControllerStatus.Running)
        {
            svc.Start();
            svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        }
    }

    public static bool ServiceExists(string name)
    {
        return ServiceController.GetServices()
            .Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow     = true,
            UseShellExecute    = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            var err = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"sc.exe {args} failed (exit {p.ExitCode}): {err}");
        }
    }
}
