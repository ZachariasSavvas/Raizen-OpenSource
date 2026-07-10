using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Microsoft.Win32;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service;

public sealed class EndpointHealthCollector
{
    internal static readonly TimeSpan ProcessInventoryInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ServiceInventoryInterval = TimeSpan.FromMinutes(2);
    private readonly object _inventoryLock = new();
    private DateTimeOffset _nextProcessInventoryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextServiceInventoryAt = DateTimeOffset.MinValue;

    public EndpointHealthSnapshotDto Collect()
    {
        var snapshot = new EndpointHealthSnapshotDto
        {
            CollectedAt = DateTimeOffset.UtcNow,
            UptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            IpAddresses = GetIpAddresses(),
            PendingReboot = IsPendingReboot(),
        };

        var errors = new List<string>();
        try { CollectOperatingSystem(snapshot); }
        catch (Exception ex) { errors.Add($"OS metrics: {Trim(ex.Message)}"); }
        try { CollectCpu(snapshot); }
        catch (Exception ex) { errors.Add($"CPU metrics: {Trim(ex.Message)}"); }
        try { CollectSystemDrive(snapshot); }
        catch (Exception ex) { errors.Add($"Disk metrics: {Trim(ex.Message)}"); }
        CollectDueInventory(snapshot, errors);

        // Defender and BitLocker are optional Windows components. An unavailable
        // provider is represented as unknown rather than making the whole snapshot fail.
        TryCollectDefender(snapshot);
        TryCollectBitLocker(snapshot);

        snapshot.CollectionError = errors.Count == 0 ? null : string.Join("; ", errors);
        return snapshot;
    }

    private void CollectDueInventory(EndpointHealthSnapshotDto snapshot, List<string> errors)
    {
        lock (_inventoryLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= _nextProcessInventoryAt)
            {
                try
                {
                    snapshot.Processes = CollectProcesses();
                    snapshot.ProcessesCollected = true;
                    _nextProcessInventoryAt = now.Add(ProcessInventoryInterval);
                }
                catch (Exception ex) { errors.Add($"Process inventory: {Trim(ex.Message)}"); }
            }

            if (now >= _nextServiceInventoryAt)
            {
                try
                {
                    snapshot.Services = CollectServices();
                    snapshot.ServicesCollected = true;
                    _nextServiceInventoryAt = now.Add(ServiceInventoryInterval);
                }
                catch (Exception ex) { errors.Add($"Service inventory: {Trim(ex.Message)}"); }
            }
        }
    }

    private static void CollectOperatingSystem(EndpointHealthSnapshotDto snapshot)
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
        using var results = searcher.Get();
        var os = results.Cast<ManagementObject>().FirstOrDefault();
        if (os is null) return;

        var total = Convert.ToDouble(os["TotalVisibleMemorySize"] ?? 0);
        var free = Convert.ToDouble(os["FreePhysicalMemory"] ?? 0);
        if (total > 0)
            snapshot.MemoryUsedPercent = Math.Round((total - free) / total * 100, 1);

        using var computerSearcher = new ManagementObjectSearcher(
            "root\\CIMV2", "SELECT UserName FROM Win32_ComputerSystem");
        using var computerResults = computerSearcher.Get();
        snapshot.LoggedOnUser = computerResults.Cast<ManagementObject>()
            .Select(x => x["UserName"]?.ToString())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static void CollectCpu(EndpointHealthSnapshotDto snapshot)
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2", "SELECT LoadPercentage FROM Win32_Processor");
        using var results = searcher.Get();
        var values = results.Cast<ManagementObject>()
            .Select(x => x["LoadPercentage"])
            .Where(x => x is not null)
            .Select(Convert.ToDouble)
            .ToList();
        if (values.Count > 0)
            snapshot.CpuLoadPercent = Math.Round(values.Average(), 1);
    }

    private static void CollectSystemDrive(EndpointHealthSnapshotDto snapshot)
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root)) return;
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.TotalSize <= 0) return;
        snapshot.SystemDriveFreeBytes = drive.AvailableFreeSpace;
        snapshot.SystemDriveFreePercent = Math.Round(
            (double)drive.AvailableFreeSpace / drive.TotalSize * 100, 1);
    }

    private static void TryCollectDefender(EndpointHealthSnapshotDto snapshot)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\Microsoft\\Windows\\Defender",
                "SELECT AntivirusEnabled,RealTimeProtectionEnabled,AntivirusSignatureAge FROM MSFT_MpComputerStatus");
            using var results = searcher.Get();
            var status = results.Cast<ManagementObject>().FirstOrDefault();
            if (status is null) return;
            var antivirus = Convert.ToBoolean(status["AntivirusEnabled"] ?? false);
            var realtime = Convert.ToBoolean(status["RealTimeProtectionEnabled"] ?? false);
            snapshot.DefenderEnabled = antivirus && realtime;
            snapshot.DefenderSignatureAgeDays = Convert.ToInt32(status["AntivirusSignatureAge"] ?? 0);
        }
        catch { }
    }

    private static void TryCollectBitLocker(EndpointHealthSnapshotDto snapshot)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(root)) return;
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2\\Security\\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{root}'");
            using var results = searcher.Get();
            var volume = results.Cast<ManagementObject>().FirstOrDefault();
            if (volume is not null)
                snapshot.BitLockerProtected = Convert.ToUInt32(volume["ProtectionStatus"] ?? 0) == 1;
        }
        catch { }
    }

    private static List<string> GetIpAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(x => !IPAddress.IsLoopback(x))
                .Select(x => x.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(16)
                .ToList();
        }
        catch { return []; }
    }

    internal static List<EndpointProcessDto> CollectProcesses()
    {
        var output = new List<EndpointProcessDto>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    output.Add(new EndpointProcessDto
                    {
                        ProcessId = process.Id,
                        Name = process.ProcessName,
                        WorkingSetBytes = Math.Max(0, process.WorkingSet64),
                        TotalProcessorTimeSeconds = Math.Max(0, process.TotalProcessorTime.TotalSeconds),
                        SessionId = process.SessionId,
                    });
                }
                catch
                {
                    // Protected and short-lived processes may disappear while enumerating.
                }
            }
        }

        return output
            .OrderByDescending(x => x.WorkingSetBytes)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
    }

    internal static List<EndpointServiceDto> CollectServices()
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name,DisplayName,State,StartMode FROM Win32_Service");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .Select(x => new EndpointServiceDto
            {
                Name = x["Name"]?.ToString() ?? string.Empty,
                DisplayName = x["DisplayName"]?.ToString() ?? string.Empty,
                Status = x["State"]?.ToString() ?? "Unknown",
                StartMode = x["StartMode"]?.ToString() ?? "Unknown",
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
    }

    internal static bool IsPendingReboot()
    {
        try
        {
            if (Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null)
                return true;
            if (Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null)
                return true;
            using var sessionManager = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch { return false; }
    }

    private static string Trim(string value) => value.Length <= 180 ? value : value[..180];
}
