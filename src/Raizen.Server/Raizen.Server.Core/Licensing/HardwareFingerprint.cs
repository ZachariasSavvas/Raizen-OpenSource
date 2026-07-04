using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Raizen.Server.Core.Licensing;

/// <summary>
/// Generates a stable hardware fingerprint for the current machine by SHA-256 hashing
/// a combination of: CPU ProcessorId, primary disk serial number, primary MAC address,
/// and OS machine GUID — making MAC-only spoofing attacks impractical.
/// </summary>
public static class HardwareFingerprint
{
    /// <summary>
    /// Returns a 64-char lowercase hex string that uniquely identifies this machine.
    /// Safe to call on any platform; unavailable components are silently skipped.
    /// </summary>
    public static string Get()
    {
        var parts = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            parts.Add(GetWindowsCpuId());
            parts.Add(GetWindowsDiskSerial());
            parts.Add(GetWindowsMachineGuid());
        }
        else
        {
            parts.Add(GetLinuxMachineId());
        }

        parts.Add(GetPrimaryMac());

        var raw  = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsCpuId()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessorId FROM Win32_Processor");
            foreach (var obj in searcher.Get())
                return obj["ProcessorId"]?.ToString()?.Trim() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsDiskSerial()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT SerialNumber FROM Win32_DiskDrive WHERE MediaType='Fixed hard disk media'");
            foreach (var obj in searcher.Get())
                return obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string GetLinuxMachineId()
    {
        try
        {
            if (File.Exists("/etc/machine-id"))
                return File.ReadAllText("/etc/machine-id").Trim();
            if (File.Exists("/var/lib/dbus/machine-id"))
                return File.ReadAllText("/var/lib/dbus/machine-id").Trim();
        }
        catch { }
        return string.Empty;
    }

    private static string GetPrimaryMac()
    {
        var virtualOuis = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "005056", "000c29", "080027", "00155d" };

        // Exclude Bluetooth PAN adapters (Windows reports them as Ethernet type but
        // they can vanish across reboots) and non-physical interface types.
        // Sort by name for deterministic selection. Do NOT filter by OperationalStatus.
        var mac = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                !ni.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .OrderBy(ni => ni.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ni => ni.GetPhysicalAddress().GetAddressBytes())
            .Where(b => b.Length == 6 && b.Any(x => x != 0))
            .Where(b => !virtualOuis.Contains($"{b[0]:x2}{b[1]:x2}{b[2]:x2}"))
            .Select(b => string.Join(":", b.Select(x => x.ToString("x2"))))
            .FirstOrDefault();

        // Fallback: any NIC with a valid MAC (includes virtual/Bluetooth)
        if (mac is null)
        {
            mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(ni => ni.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ni => ni.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b.Length == 6 && b.Any(x => x != 0))
                .Select(b => string.Join(":", b.Select(x => x.ToString("x2"))))
                .FirstOrDefault();
        }

        return mac ?? string.Empty;
    }
}
