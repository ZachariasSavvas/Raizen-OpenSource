using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Writes a DWORD or String registry value to an allowed key.
///
/// Required parameters:
///   Hive         — HKLM or HKCU
///   SubKey       — Registry subkey path (e.g. "SOFTWARE\MyApp\Settings")
///   ValueName    — Name of the registry value
///   ValueData    — The data to write
///   ValueKind    — String or DWord
///
/// Security: subkey must begin with an approved prefix. Write to HKLM\SYSTEM,
/// HKLM\SAM, or security-sensitive keys is blocked.
/// </summary>
public sealed class RegistryHandler(ILogger<RegistryHandler> log) : IActionHandler
{
    private static readonly HashSet<string> BlockedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Credential and authentication infrastructure
        @"SYSTEM\CurrentControlSet\Control\Lsa",
        @"SAM",
        @"SECURITY",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        // Persistence mechanisms
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        // Service binary paths — modifying ImagePath could replace a service executable
        @"SYSTEM\CurrentControlSet\Services",
        // Image File Execution Options — debugger hijacking / process substitution
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
    };

    private static readonly Regex SafeSubKey = new(
        @"^[A-Za-z0-9_.\\\- ]{1,512}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex SafeValueName = new(
        @"^[A-Za-z0-9_.\- ]{0,256}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    public ActionType HandledType => ActionType.SetRegistryValue;

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var hive = request.Parameters.GetValueOrDefault("Hive", "HKLM").ToUpperInvariant();
        var subKey = request.Parameters.GetValueOrDefault("SubKey", "");
        var valueName = request.Parameters.GetValueOrDefault("ValueName", "");
        var valueData = request.Parameters.GetValueOrDefault("ValueData", "");
        var valueKind = request.Parameters.GetValueOrDefault("ValueKind", "String");

        if (!SafeSubKey.IsMatch(subKey))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"SubKey '{subKey}' is invalid."));
        if (!SafeValueName.IsMatch(valueName))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"ValueName '{valueName}' is invalid."));

        // Block sensitive paths
        foreach (var blocked in BlockedPrefixes)
        {
            if (subKey.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ActionResult(false,
                    ErrorMessage: $"Registry subkey '{subKey}' is in the deny list."));
        }

        try
        {
            RegistryKey root = hive switch
            {
                "HKLM" => Registry.LocalMachine,
                "HKCU" => Registry.CurrentUser,
                _ => throw new ArgumentException($"Unsupported hive: {hive}")
            };

            using var key = root.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Cannot open/create key: {hive}\\{subKey}");

            if (valueKind.Equals("DWord", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(valueData, out var dword))
                    return Task.FromResult(new ActionResult(false,
                        ErrorMessage: $"ValueData '{valueData}' is not a valid DWORD."));
                key.SetValue(valueName, (int)dword, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(valueName, valueData, RegistryValueKind.String);
            }

            log.LogInformation("[Request:{RequestId}] Set registry {Hive}\\{SubKey}\\{Name}",
                request.Id, hive, subKey, valueName);
            return Task.FromResult(new ActionResult(true,
                ResultMessage: $"Set {hive}\\{subKey}\\{valueName} = {valueData}"));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] Registry set failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Registry operation failed. See endpoint logs for details."));
        }
    }
}
