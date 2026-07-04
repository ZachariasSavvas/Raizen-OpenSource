using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Copies a file from an approved source path to an approved destination path.
///
/// Required parameters:
///   SourcePath      — Absolute path or UNC of the source file
///   DestinationPath — Absolute path of the destination file or directory
///
/// Security: no shell invocation; path traversal is rejected.
/// </summary>
public sealed class CopyFileHandler(ILogger<CopyFileHandler> log) : IActionHandler, IPreflightCheck
{
    // Reject paths with ..\ or dangerous characters; allow bare drive roots like D:\
    private static readonly Regex SafePath = new(
        @"^(\\\\[\w\.\-]+\\[\w\.\-$\\]+|[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]*)$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    public ActionType HandledType => ActionType.CopyFile;

    public string? Validate(ElevationRequestDto request)
    {
        var source = request.Parameters.GetValueOrDefault("SourcePath", "");
        if (string.IsNullOrWhiteSpace(source)) return "SourcePath parameter is required.";
        return File.Exists(source) ? null : $"Source file not found: {source}";
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var source = request.Parameters.GetValueOrDefault("SourcePath", "");
        var dest = request.Parameters.GetValueOrDefault("DestinationPath", "");

        // Canonicalize to resolve any .., ., //, etc. — then reject if the path changed
        // (which indicates the caller encoded traversal components in the path).
        if (!TryCanonicalise(ref source, out var srcErr))
            return Task.FromResult(new ActionResult(false, ErrorMessage: srcErr));
        if (!TryCanonicalise(ref dest, out var dstErr))
            return Task.FromResult(new ActionResult(false, ErrorMessage: dstErr));

        if (!SafePath.IsMatch(source))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"SourcePath '{source}' is invalid."));
        if (!SafePath.IsMatch(dest))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"DestinationPath '{dest}' is invalid."));
        if (!File.Exists(source))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Source file not found: {source}"));

        var operation = request.Parameters.GetValueOrDefault("Operation", "Copy");
        var isMove = string.Equals(operation, "Move", StringComparison.OrdinalIgnoreCase);

        try
        {
            // If dest is a directory, preserve the original filename
            if (Directory.Exists(dest))
                dest = Path.Combine(dest, Path.GetFileName(source));

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (isMove)
            {
                File.Move(source, dest, overwrite: true);
                log.LogInformation("[Request:{RequestId}] Moved '{Source}' → '{Dest}'", request.Id, source, dest);
                return Task.FromResult(new ActionResult(true, ResultMessage: $"Moved to '{dest}'."));
            }
            else
            {
                File.Copy(source, dest, overwrite: true);
                log.LogInformation("[Request:{RequestId}] Copied '{Source}' → '{Dest}'", request.Id, source, dest);
                return Task.FromResult(new ActionResult(true, ResultMessage: $"Copied to '{dest}'."));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] CopyFile failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "File copy operation failed. See endpoint logs for details."));
        }
    }

    /// <summary>
    /// Canonicalises <paramref name="path"/> via <see cref="Path.GetFullPath"/>.
    /// Returns <c>false</c> (with an error message) if the path is syntactically invalid
    /// or if canonicalisation changed the path, which indicates embedded traversal sequences.
    /// </summary>
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

        path  = canonical;
        error = string.Empty;
        return true;
    }
}
