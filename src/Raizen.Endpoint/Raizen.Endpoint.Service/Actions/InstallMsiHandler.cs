using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Installs an MSI or MSIX package using msiexec.exe with a controlled, auditable
/// set of arguments. No interactive UI is shown (quiet mode).
///
/// Required parameters:
///   PackagePath  — full UNC or local path to the .msi file
///   ProductName  — display name for logging only
///
/// Optional parameters:
///   MsiProperties — additional PROPERTY=VALUE pairs (e.g. "REBOOT=ReallySuppress")
///                   Space-separated; values must not contain semicolons or quotes.
///
/// Security: msiexec.exe is called directly — NOT via cmd or powershell.
/// Path is validated against an allowlist pattern before execution.
/// </summary>
public sealed class InstallMsiHandler(ILogger<InstallMsiHandler> log) : IActionHandler, IPreflightCheck
{
    // Allowlist: UNC paths (\\server\share\file.msi) or local absolute paths with no ..\
    private static readonly Regex AllowedPath = new(
        @"^(\\\\[\w\.\-]+\\[\w\.\-$\\]+|[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]+)\.msi$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    // Only allow safe MSI property tokens (UPPER=value, no cmd injection chars)
    private static readonly Regex SafeProperty = new(
        @"^[A-Z_][A-Z0-9_]*=[^;""<>|&\r\n]{0,256}$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    public ActionType HandledType => ActionType.InstallMsi;

    public string? Validate(ElevationRequestDto request)
    {
        var path = request.Parameters.GetValueOrDefault("PackagePath", "");
        if (string.IsNullOrWhiteSpace(path)) return "PackagePath parameter is required.";
        return File.Exists(path) ? null : $"Package file not found: {path}";
    }

    public async Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var packagePath = request.Parameters.GetValueOrDefault("PackagePath", "");
        var productName = request.Parameters.GetValueOrDefault("ProductName", "Unknown");
        var extraProps = request.Parameters.GetValueOrDefault("MsiProperties", "");

        if (!AllowedPath.IsMatch(packagePath))
            return new ActionResult(false, ErrorMessage: $"PackagePath '{packagePath}' is not an allowed MSI path.");

        if (!File.Exists(packagePath))
            return new ActionResult(false, ErrorMessage: $"Package file not found: {packagePath}");

        // Validate extra properties
        var propArgs = "";
        if (!string.IsNullOrWhiteSpace(extraProps))
        {
            var props = extraProps.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var prop in props)
            {
                if (!SafeProperty.IsMatch(prop))
                    return new ActionResult(false, ErrorMessage: $"Unsafe MSI property: {prop}");
            }
            propArgs = " " + string.Join(" ", props);
        }

        // msiexec.exe arguments — no shell; double-quote path, quiet install, no reboot
        var args = $"/i \"{packagePath}\" /qn /norestart REBOOT=ReallySuppress{propArgs}";

        log.LogInformation(
            "[Request:{RequestId}] Installing MSI: {ProductName} from {Path}",
            request.Id, productName, packagePath);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start msiexec.exe");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0)
        {
            log.LogInformation("[Request:{RequestId}] MSI install succeeded.", request.Id);
            return new ActionResult(true, ResultMessage: $"Successfully installed '{productName}'.");
        }

        // msiexec exit codes: 3010 = success, requires reboot
        if (process.ExitCode == 3010)
        {
            log.LogInformation("[Request:{RequestId}] MSI installed (reboot required).", request.Id);
            return new ActionResult(true, ResultMessage: $"Installed '{productName}' — reboot required.");
        }

        log.LogError("[Request:{RequestId}] msiexec exited with code {Code}", request.Id, process.ExitCode);
        return new ActionResult(false, ErrorMessage: $"msiexec exited with code {process.ExitCode}.");
    }
}
