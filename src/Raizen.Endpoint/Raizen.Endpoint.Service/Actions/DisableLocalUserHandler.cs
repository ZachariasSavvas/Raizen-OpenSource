using System.DirectoryServices.AccountManagement;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Disables (locks) a local Windows user account.
///
/// Required parameters:
///   Username — Local account name to disable
///
/// Security: uses DirectoryServices.AccountManagement, no shell invocation.
/// Built-in accounts (Administrator, Guest, etc.) cannot be disabled.
/// </summary>
public sealed class DisableLocalUserHandler(ILogger<DisableLocalUserHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex SafeUsername = new(
        @"^[A-Za-z0-9_\.\-]{1,20}$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ProtectedAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount",
        "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
    };

    public ActionType HandledType => ActionType.DisableLocalUser;

    public string? Validate(ElevationRequestDto request)
    {
        var username = request.Parameters.GetValueOrDefault("Username", "");
        if (string.IsNullOrWhiteSpace(username))
            return "Username parameter is required.";
        if (!SafeUsername.IsMatch(username))
            return $"Username '{username}' contains invalid characters.";
        if (ProtectedAccounts.Contains(username))
            return $"Cannot disable protected built-in account '{username}'.";
        return null;
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var username = request.Parameters.GetValueOrDefault("Username", "");

        if (!SafeUsername.IsMatch(username))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Username '{username}' is invalid."));
        if (ProtectedAccounts.Contains(username))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Cannot disable protected account '{username}'."));

        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);

            if (user is null)
                return Task.FromResult(new ActionResult(false, ErrorMessage: $"User '{username}' not found."));

            if (user.Enabled == false)
                return Task.FromResult(new ActionResult(true, ResultMessage: $"User '{username}' is already disabled."));

            user.Enabled = false;
            user.Save();

            log.LogInformation("[Request:{RequestId}] Disabled local user '{Username}'", request.Id, username);
            return Task.FromResult(new ActionResult(true, ResultMessage: $"Disabled local user '{username}'."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] DisableLocalUser failed for '{Username}'", request.Id, username);
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Failed to disable user '{username}'. See endpoint logs for details."));
        }
    }
}
