using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Deletes a file or directory from an approved path.
///
/// Required parameters:
///   FilePath — Absolute path to the file or directory to delete
///
/// Optional parameters:
///   Recursive — "true" to delete directories recursively (default: "false")
///
/// Security: no shell invocation; path traversal is rejected.
/// </summary>
public sealed class DeleteFileHandler(ILogger<DeleteFileHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex SafePath = new(
        @"^(\\\\[\w\.\-]+\\[\w\.\-$\\]+|[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]*)$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    // Block deletion of critical system paths
    private static readonly HashSet<string> BlockedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows",
        @"C:\Windows\System32",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users",
        @"C:\ProgramData",
    };

    public ActionType HandledType => ActionType.DeleteFile;

    public string? Validate(ElevationRequestDto request)
    {
        var path = request.Parameters.GetValueOrDefault("FilePath", "");
        if (string.IsNullOrWhiteSpace(path))
            return "FilePath parameter is required.";
        if (!File.Exists(path) && !Directory.Exists(path))
            return $"Path not found: {path}";
        return null;
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var filePath = request.Parameters.GetValueOrDefault("FilePath", "");
        var recursive = string.Equals(
            request.Parameters.GetValueOrDefault("Recursive", "false"),
            "true", StringComparison.OrdinalIgnoreCase);

        if (!TryCanonicalise(ref filePath, out var err))
            return Task.FromResult(new ActionResult(false, ErrorMessage: err));

        if (!SafePath.IsMatch(filePath))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"FilePath '{filePath}' is invalid."));

        if (BlockedPaths.Contains(filePath))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Deletion of protected system path '{filePath}' is not permitted."));

        try
        {
            if (Directory.Exists(filePath))
            {
                if (!recursive)
                    return Task.FromResult(new ActionResult(false,
                        ErrorMessage: $"'{filePath}' is a directory. Set Recursive=true to delete directories."));

                Directory.Delete(filePath, recursive: true);
                log.LogInformation("[Request:{RequestId}] Deleted directory '{Path}' recursively", request.Id, filePath);
                return Task.FromResult(new ActionResult(true, ResultMessage: $"Deleted directory '{filePath}'."));
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                log.LogInformation("[Request:{RequestId}] Deleted file '{Path}'", request.Id, filePath);
                return Task.FromResult(new ActionResult(true, ResultMessage: $"Deleted '{filePath}'."));
            }

            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Path not found: {filePath}"));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] DeleteFile failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Delete operation failed. See endpoint logs for details."));
        }
    }

    private static bool TryCanonicalise(ref string path, out string error)
    {
        string canonical;
        try { canonical = Path.GetFullPath(path); }
        catch
        {
            error = $"Path '{path}' is not a valid file system path.";
            return false;
        }

        if (!string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Path must be fully qualified with no traversal sequences (got '{path}').";
            return false;
        }

        path = canonical;
        error = string.Empty;
        return true;
    }
}
