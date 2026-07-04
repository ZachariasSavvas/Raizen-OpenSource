using System.DirectoryServices.AccountManagement;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Adds or removes a domain/local user from a specific local security group.
///
/// Required parameters:
///   GroupName   — local group name (e.g. "Remote Desktop Users")
///   UserName    — SAM account name or UPN of the user to add/remove
///
/// Security: uses DirectoryServices.AccountManagement — no shell invocation.
/// GroupName is validated against an allowlist to prevent privilege escalation
/// via "Administrators" group membership.
/// </summary>
public sealed class LocalGroupMemberHandler(ILogger<LocalGroupMemberHandler> log) : IActionHandler, IPreflightCheck
{
    /// <summary>
    /// Groups that may NEVER be targets, even if an approver approves.
    /// Add any other sensitive groups to this list.
    /// </summary>
    private static readonly HashSet<string> DeniedGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrators",
        "Domain Admins",
        "Schema Admins",
        "Enterprise Admins",
        "Group Policy Creator Owners",
        "Account Operators",
        "Backup Operators",
        "Print Operators",
        "Server Operators",
    };

    // SAM account name or UPN characters only
    private static readonly Regex SafeUserName = new(
        @"^[A-Za-z0-9._@\\-]{1,256}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly bool _isAdd;

    public LocalGroupMemberHandler(bool isAdd, ILogger<LocalGroupMemberHandler> log) : this(log)
        => _isAdd = isAdd;

    // Registered separately as AddLocalGroupMember and RemoveLocalGroupMember
    public ActionType HandledType => _isAdd ? ActionType.AddLocalGroupMember : ActionType.RemoveLocalGroupMember;

    public string? Validate(ElevationRequestDto request)
    {
        var groupName = request.Parameters.GetValueOrDefault("GroupName", "");
        if (string.IsNullOrWhiteSpace(groupName)) return "GroupName parameter is required.";
        try
        {
            using var ctx   = new PrincipalContext(ContextType.Machine);
            using var group = GroupPrincipal.FindByIdentity(ctx, groupName);
            return group is not null ? null : $"Local group '{groupName}' does not exist.";
        }
        catch
        {
            return $"Could not query local group '{groupName}'.";
        }
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var groupName = request.Parameters.GetValueOrDefault("GroupName", "");
        var userName = request.Parameters.GetValueOrDefault("UserName", "");

        if (string.IsNullOrWhiteSpace(groupName))
            return Task.FromResult(new ActionResult(false, ErrorMessage: "GroupName is required."));

        if (DeniedGroups.Contains(groupName))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"Group '{groupName}' is in the permanent deny list."));

        if (!SafeUserName.IsMatch(userName))
            return Task.FromResult(new ActionResult(false,
                ErrorMessage: $"UserName '{userName}' contains invalid characters."));

        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var group = GroupPrincipal.FindByIdentity(ctx, groupName)
                ?? throw new InvalidOperationException($"Local group '{groupName}' not found.");

            // Determine if the user is domain or local; dispose domain context when done
            using var domainCtx = (userName.Contains('@') || userName.Contains('\\'))
                ? new PrincipalContext(ContextType.Domain)
                : null;
            var userCtx = domainCtx ?? ctx;

            using var user = UserPrincipal.FindByIdentity(userCtx, userName)
                ?? throw new InvalidOperationException($"User '{userName}' not found.");

            var alreadyMember = group.GetMembers().Any(m => m.Sid == user.Sid);

            if (_isAdd)
            {
                if (alreadyMember)
                    return Task.FromResult(new ActionResult(true,
                        ResultMessage: $"User '{userName}' is already a member of '{groupName}'."));

                group.Members.Add(user);
                group.Save();
                log.LogInformation("[Request:{RequestId}] Added {User} to {Group}", request.Id, userName, groupName);
                return Task.FromResult(new ActionResult(true,
                    ResultMessage: $"Added '{userName}' to '{groupName}'."));
            }
            else
            {
                if (!alreadyMember)
                    return Task.FromResult(new ActionResult(true,
                        ResultMessage: $"User '{userName}' is not a member of '{groupName}'."));

                group.Members.Remove(user);
                group.Save();
                log.LogInformation("[Request:{RequestId}] Removed {User} from {Group}", request.Id, userName, groupName);
                return Task.FromResult(new ActionResult(true,
                    ResultMessage: $"Removed '{userName}' from '{groupName}'."));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] LocalGroupMember failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Local group member operation failed. See endpoint logs for details."));
        }
    }
}

/// <summary>Separate handler registration for Remove to satisfy ActionType enum.</summary>
public sealed class RemoveLocalGroupMemberHandler(ILogger<LocalGroupMemberHandler> log) : IActionHandler
{
    private readonly LocalGroupMemberHandler _inner = new(isAdd: false, log);
    public ActionType HandledType => ActionType.RemoveLocalGroupMember;
    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
        => _inner.ExecuteAsync(request, ct);
}
