using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Uninstalls an MSI package by its product code using msiexec.exe.
/// No interactive UI is shown (quiet mode).
///
/// Required parameters:
///   ProductCode  — MSI product code GUID (e.g. {12345678-1234-1234-1234-123456789ABC})
///   ProductName  — display name for logging only
///
/// Security: msiexec.exe is called directly, NOT via cmd or powershell.
/// Product code is validated as a strict GUID format before execution.
/// </summary>
public sealed class UninstallMsiHandler(ILogger<UninstallMsiHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex ProductCodePattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    public ActionType HandledType => ActionType.UninstallMsi;

    public string? Validate(ElevationRequestDto request)
    {
        var productCode = request.Parameters.GetValueOrDefault("ProductCode", "");
        if (string.IsNullOrWhiteSpace(productCode))
            return "ProductCode parameter is required.";
        if (!ProductCodePattern.IsMatch(productCode))
            return $"ProductCode '{productCode}' is not a valid MSI product code GUID.";
        return null;
    }

    public async Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var productCode = request.Parameters.GetValueOrDefault("ProductCode", "");
        var productName = request.Parameters.GetValueOrDefault("ProductName", "Unknown");

        if (!ProductCodePattern.IsMatch(productCode))
            return new ActionResult(false, ErrorMessage: $"ProductCode '{productCode}' is not a valid MSI product code GUID.");

        var args = $"/x \"{productCode}\" /qn /norestart REBOOT=ReallySuppress";

        log.LogInformation(
            "[Request:{RequestId}] Uninstalling MSI: {ProductName} ({ProductCode})",
            request.Id, productName, productCode);

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
            log.LogInformation("[Request:{RequestId}] MSI uninstall succeeded.", request.Id);
            return new ActionResult(true, ResultMessage: $"Successfully uninstalled '{productName}'.");
        }

        if (process.ExitCode == 3010)
        {
            log.LogInformation("[Request:{RequestId}] MSI uninstalled (reboot required).", request.Id);
            return new ActionResult(true, ResultMessage: $"Uninstalled '{productName}'; reboot required.");
        }

        log.LogError("[Request:{RequestId}] msiexec exited with code {Code}", request.Id, process.ExitCode);
        return new ActionResult(false, ErrorMessage: $"msiexec exited with code {process.ExitCode}.");
    }
}
