using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Applies a specific NTFS permission change to a file or folder as SYSTEM.
///
/// Required parameters:
///   FilePath        — Absolute local path to the file or folder.
///   Account         — Canonical account name in DOMAIN\username or MACHINE\username form.
///   PermissionLevel — Read | ReadAndExecute | FullControl | Remove
///   ApplyTo         — FolderSubfoldersAndFiles | FolderOnly | FilesOnly
///                     (ignored for files; only meaningful for directories)
///
/// Security:
///   - Path traversal sequences are rejected.
///   - Shell meta-characters are rejected.
///   - File or directory must exist at execution time.
///   - Account is resolved to a SID before any ACL operation to detect invalid accounts early.
/// </summary>
public sealed class FilePermissionsHandler(ILogger<FilePermissionsHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex SafePath = new(
        @"^[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]+$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    public ActionType HandledType => ActionType.OpenFileProperties;

    public string? Validate(ElevationRequestDto request)
    {
        var path = request.Parameters.GetValueOrDefault("FilePath", "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return "FilePath parameter is required.";
        return (File.Exists(path) || Directory.Exists(path)) ? null : $"Path not found: {path}";
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        // ── Read parameters ────────────────────────────────────────────────────
        var filePath     = request.Parameters.GetValueOrDefault("FilePath",        "").Trim().Trim('"');
        var accountName  = request.Parameters.GetValueOrDefault("Account",         "").Trim();
        var permLevelStr = request.Parameters.GetValueOrDefault("PermissionLevel", "FullControl").Trim();
        var applyToStr   = request.Parameters.GetValueOrDefault("ApplyTo",         "FolderSubfoldersAndFiles").Trim();

        // ── Validate path ──────────────────────────────────────────────────────
        // Canonicalize first to detect encoded traversal sequences (.., ., //, etc.)
        string canonical;
        try { canonical = Path.GetFullPath(filePath); }
        catch
        {
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "FilePath is not a valid file system path."));
        }

        if (!string.Equals(canonical, filePath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "FilePath must be fully qualified with no traversal sequences."));

        filePath = canonical;

        if (!SafePath.IsMatch(filePath))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"FilePath '{filePath}' contains invalid characters or traversal sequences."));

        bool isDir  = Directory.Exists(filePath);
        bool isFile = !isDir && File.Exists(filePath);

        if (!isDir && !isFile)
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"Path not found: {filePath}"));

        // ── Validate account ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(accountName))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "Account parameter is required."));

        SecurityIdentifier sid;
        try
        {
            var ntAccount = new NTAccount(accountName);
            sid = (SecurityIdentifier)ntAccount.Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{Id}] Could not resolve account '{Account}'.", request.Id, accountName);
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "Could not resolve the specified account. See endpoint logs for details."));
        }

        // ── Map permission level ───────────────────────────────────────────────
        bool isRemove = permLevelStr.Equals("Remove", StringComparison.OrdinalIgnoreCase);

        FileSystemRights rights = permLevelStr.ToLowerInvariant() switch
        {
            "read"           => FileSystemRights.Read,
            "readandexecute" => FileSystemRights.ReadAndExecute,
            _                => FileSystemRights.FullControl,   // FullControl or unrecognised
        };

        // ── Map apply-to scope ─────────────────────────────────────────────────
        var (inheritFlags, propFlags) = applyToStr.ToLowerInvariant() switch
        {
            "folderonly" => (InheritanceFlags.None, PropagationFlags.None),
            "filesonly"  => (InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
            _            => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                             PropagationFlags.None),  // FolderSubfoldersAndFiles (default)
        };

        // ── Apply ACL ──────────────────────────────────────────────────────────
        try
        {
            if (isDir)
                ApplyDirectoryAcl(filePath, sid, isRemove, rights, inheritFlags, propFlags);
            else
                ApplyFileAcl(filePath, sid, isRemove, rights);

            var opText = isRemove
                ? $"Removed access for '{accountName}'"
                : $"Granted {permLevelStr} to '{accountName}'";

            log.LogInformation(
                "[Request:{Id}] {Op} on '{Path}' (ApplyTo={ApplyTo}).",
                request.Id, opText, filePath, applyToStr);

            return Task.FromResult(new ActionResult(true,
                ResultMessage: $"{opText} on '{Path.GetFileName(filePath)}'."));
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogError(ex, "[Request:{Id}] Access denied applying permissions on '{Path}'.", request.Id, filePath);
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: "Access denied applying permissions. See endpoint logs for details."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{Id}] FilePermissions failed for '{Path}'.", request.Id, filePath);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "File permissions operation failed. See endpoint logs for details."));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void ApplyDirectoryAcl(
        string path, SecurityIdentifier sid,
        bool isRemove, FileSystemRights rights,
        InheritanceFlags inherit, PropagationFlags propagation)
    {
        var di       = new DirectoryInfo(path);
        var security = di.GetAccessControl();

        if (isRemove)
        {
            // Remove all explicit Allow rules for this SID regardless of inheritance/propagation flags
            var rulesToRemove = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(r => r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow)
                .ToList();
            foreach (var rule in rulesToRemove)
                security.RemoveAccessRule(rule);
        }
        else
        {
            var rule = new FileSystemAccessRule(
                sid, rights, inherit, propagation, AccessControlType.Allow);
            security.AddAccessRule(rule);
        }

        di.SetAccessControl(security);
    }

    private static void ApplyFileAcl(
        string path, SecurityIdentifier sid,
        bool isRemove, FileSystemRights rights)
    {
        var fi       = new FileInfo(path);
        var security = fi.GetAccessControl();

        if (isRemove)
        {
            var rulesToRemove = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(r => r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow)
                .ToList();
            foreach (var rule in rulesToRemove)
                security.RemoveAccessRule(rule);
        }
        else
        {
            var rule = new FileSystemAccessRule(sid, rights, AccessControlType.Allow);
            security.AddAccessRule(rule);
        }

        fi.SetAccessControl(security);
    }
}
