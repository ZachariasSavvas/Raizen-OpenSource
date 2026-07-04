using System.Diagnostics;

namespace Raizen.Server.Setup.Installer;

public static class FirewallSetup
{
    public static void AddRules(int apiPort, int webPort)
    {
        // Remove stale rules if they exist so the port number stays current
        RunNetsh("advfirewall firewall delete rule name=\"Raizen API\"");
        RunNetsh("advfirewall firewall delete rule name=\"Raizen Web Portal\"");

        RunNetsh($"advfirewall firewall add rule name=\"Raizen API\" " +
                 $"dir=in action=allow protocol=TCP localport={apiPort} " +
                 $"description=\"Raizen elevation API inbound\"");

        RunNetsh($"advfirewall firewall add rule name=\"Raizen Web Portal\" " +
                 $"dir=in action=allow protocol=TCP localport={webPort} " +
                 $"description=\"Raizen admin portal inbound\"");
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow  = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        // Ignore exit code for delete (fails if rule doesn't exist — that's OK)
    }
}
