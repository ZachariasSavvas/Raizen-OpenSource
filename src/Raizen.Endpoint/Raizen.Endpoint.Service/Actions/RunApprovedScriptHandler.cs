using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Executes a pre-approved, immutable script whose SHA-256 hash is recorded in the
/// action definition.  The script must be a .bat or .ps1 file located in the
/// approved scripts directory (configurable via RAIZEN_SCRIPTS_DIR).
///
/// Required parameters:
///   ScriptFileName  — filename only (no path separators), e.g. "deploy-config.bat"
///   ExpectedSha256  — lowercase hex SHA-256 of the script file
///
/// Optional parameters:
///   ScriptArgs      — space-separated arguments (must not include pipe, redirect, etc.)
///
/// SECURITY NOTES:
///   • The script file hash is verified before execution.
///   • .ps1 files are executed as: powershell.exe -NonInteractive -NoProfile
///       -ExecutionPolicy Bypass -File "script.ps1"   (no -Command, no -EncodedCommand)
///   • .bat files are executed as: cmd.exe /D /Q /C "script.bat"
///     /D disables AutoRun; /Q enables echo off; /C runs and exits.
///   • ScriptArgs must match a safe regex — no pipes, redirects, or ampersands.
///   • Scripts directory is NOT user-writable (enforced at install time via ACLs).
///
/// Note: This is the ONLY handler that may invoke cmd or powershell, and only for
/// pre-approved, hash-verified script files located in an admin-controlled directory.
/// </summary>
public sealed class RunApprovedScriptHandler(ILogger<RunApprovedScriptHandler> log, ConfigLoader configLoader) : IActionHandler
{
    private static readonly string ScriptsDir =
        Environment.GetEnvironmentVariable("RAIZEN_SCRIPTS_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Raizen", "ApprovedScripts");

    // Filename only, no path separators
    private static readonly Regex SafeFileName = new(
        @"^[A-Za-z0-9_\-\.]{1,128}\.(bat|ps1)$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    // Hex SHA-256
    private static readonly Regex SafeHash = new(
        @"^[0-9a-f]{64}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    // Safe arguments: no shell metacharacters
    private static readonly Regex SafeArgs = new(
        @"^[A-Za-z0-9_\-\. =:/\\]{0,512}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    public ActionType HandledType => ActionType.RunApprovedScript;

    public async Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var fileName = request.Parameters.GetValueOrDefault("ScriptFileName", "");
        var expectedHash = request.Parameters.GetValueOrDefault("ExpectedSha256", "").ToLowerInvariant();
        var scriptArgs = request.Parameters.GetValueOrDefault("ScriptArgs", "");

        if (!SafeFileName.IsMatch(fileName))
            return new ActionResult(false, ErrorMessage: $"ScriptFileName '{fileName}' is invalid.");
        if (!SafeHash.IsMatch(expectedHash))
            return new ActionResult(false, ErrorMessage: "ExpectedSha256 is not a valid hex SHA-256.");
        if (!string.IsNullOrEmpty(scriptArgs) && !SafeArgs.IsMatch(scriptArgs))
            return new ActionResult(false, ErrorMessage: "ScriptArgs contain disallowed characters.");

        var scriptPath = Path.Combine(ScriptsDir, fileName);
        if (!File.Exists(scriptPath))
            return new ActionResult(false, ErrorMessage: $"Approved script not found: {fileName}");

        // Read the entire script into memory under an exclusive lock.
        // This prevents TOCTOU: another process cannot swap the file while we hold the lock,
        // so the bytes we hash are the bytes the OS will execute.
        byte[] scriptBytes;
        try
        {
            using var fs = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.None,
                bufferSize: 65536, useAsync: true);
            using var ms = new MemoryStream((int)fs.Length);
            await fs.CopyToAsync(ms, ct);
            scriptBytes = ms.ToArray();
        }
        catch (IOException ex)
        {
            log.LogError(ex, "[Request:{RequestId}] Cannot exclusively read script '{File}'.", request.Id, fileName);
            return new ActionResult(false, ErrorMessage: "Cannot read script file. See endpoint logs for details.");
        }

        // Verify hash against in-memory bytes (not the file on disk)
        var actualHash = Convert.ToHexString(SHA256.HashData(scriptBytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualHash),
                System.Text.Encoding.ASCII.GetBytes(expectedHash)))
        {
            log.LogWarning("[Request:{RequestId}] Script hash mismatch for {File}. Expected={Expected} Actual={Actual}",
                request.Id, fileName, expectedHash, actualHash);
            return new ActionResult(false, ErrorMessage: "Script file hash does not match the approved hash.");
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        ProcessStartInfo psi;

        if (ext == ".ps1")
        {
            var psExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            var psArgs = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
            if (!string.IsNullOrEmpty(scriptArgs)) psArgs += " " + scriptArgs;
            psi = new ProcessStartInfo { FileName = psExe, Arguments = psArgs };
        }
        else // .bat
        {
            var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // /D = disable AutoRun  /Q = echo off  /C = run and exit
            var cmdArgs = $"/D /Q /C \"{scriptPath}\"";
            if (!string.IsNullOrEmpty(scriptArgs)) cmdArgs += " " + scriptArgs;
            psi = new ProcessStartInfo { FileName = cmdExe, Arguments = cmdArgs };
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = ScriptsDir;

        log.LogInformation("[Request:{RequestId}] Executing approved script '{File}'", request.Id, fileName);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start script process.");

        var timeoutMinutes = Math.Clamp(configLoader.Current.ScriptTimeoutMinutes, 1, 120);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        string stdout, stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            log.LogError("[Request:{RequestId}] Script '{File}' timed out after {Timeout} minutes and was killed.", request.Id, fileName, timeoutMinutes);
            return new ActionResult(false, ErrorMessage: $"Script execution timed out after {timeoutMinutes} minutes.");
        }

        if (process.ExitCode == 0)
        {
            log.LogInformation("[Request:{RequestId}] Script '{File}' succeeded.", request.Id, fileName);
            return new ActionResult(true, ResultMessage: stdout.Trim().Length > 0 ? stdout.Trim() : "Script completed successfully.");
        }

        log.LogError("[Request:{RequestId}] Script '{File}' exited {Code}. Stderr: {Err}",
            request.Id, fileName, process.ExitCode, stderr);
        return new ActionResult(false, ErrorMessage: $"Script exited {process.ExitCode}: {stderr.Trim()}");
    }

}
