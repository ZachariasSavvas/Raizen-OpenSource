using System.DirectoryServices.AccountManagement;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Creates a local Windows user account with a temporary one-use password.
///
/// Required parameters:
///   Username — Local account name (max 20 chars, alphanumeric + underscore/hyphen/dot)
///   Password — Initial password (user must change at first logon)
///
/// Optional parameters:
///   FullName    — Display name
///   Description — Account description
///
/// Security: uses DirectoryServices.AccountManagement, no shell invocation.
/// The account is created with UserMustChangePasswordAtLogon = true.
/// </summary>
public sealed class CreateLocalUserHandler(ILogger<CreateLocalUserHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex SafeUsername = new(
        @"^[A-Za-z0-9_\.\-]{1,20}$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    // Block creation of built-in / reserved account names
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount",
        "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
    };

    public ActionType HandledType => ActionType.CreateLocalUser;

    public string? Validate(ElevationRequestDto request)
    {
        var username = request.Parameters.GetValueOrDefault("Username", "");
        if (string.IsNullOrWhiteSpace(username))
            return "Username parameter is required.";
        if (!SafeUsername.IsMatch(username))
            return $"Username '{username}' contains invalid characters or exceeds 20 characters.";
        if (ReservedNames.Contains(username))
            return $"Cannot create reserved account '{username}'.";

        var password = request.Parameters.GetValueOrDefault("Password", "");
        if (string.IsNullOrWhiteSpace(password))
            return "Password parameter is required.";
        if (password.Length < 8)
            return "Password must be at least 8 characters.";

        return null;
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var username = request.Parameters.GetValueOrDefault("Username", "");
        var password = request.Parameters.GetValueOrDefault("Password", "");
        var fullName = request.Parameters.GetValueOrDefault("FullName", "");
        var description = request.Parameters.GetValueOrDefault("Description", "");

        if (!SafeUsername.IsMatch(username))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Username '{username}' is invalid."));
        if (ReservedNames.Contains(username))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Cannot create reserved account '{username}'."));
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Password must be at least 8 characters."));

        try
        {
            using var context = new PrincipalContext(ContextType.Machine);

            // Check if user already exists
            using var existing = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, username);
            if (existing is not null)
                return Task.FromResult(new ActionResult(false, ErrorMessage: $"User '{username}' already exists."));

            using var user = new UserPrincipal(context)
            {
                SamAccountName = username,
                DisplayName = string.IsNullOrWhiteSpace(fullName) ? username : fullName,
                Description = description,
                Enabled = true,
                PasswordNeverExpires = false,
                UserCannotChangePassword = false,
            };

            user.SetPassword(password);
            user.ExpirePasswordNow();
            user.Save();

            log.LogInformation("[Request:{RequestId}] Created local user '{Username}'", request.Id, username);
            return Task.FromResult(new ActionResult(true, ResultMessage: $"Created local user '{username}'. Password must be changed at first logon."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] CreateLocalUser failed for '{Username}'", request.Id, username);
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"Failed to create user '{username}'. See endpoint logs for details."));
        }
    }
}
