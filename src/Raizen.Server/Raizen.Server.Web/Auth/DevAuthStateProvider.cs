using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Raizen.Server.Web.Auth;

/// <summary>
/// Development-only Blazor authentication state provider.
/// Returns a hard-coded Raizen.Admin principal so all admin UI features work
/// without a real Entra ID tenant configured.
/// Only registered when DevMode:Enabled = true in appsettings.Development.json.
/// </summary>
public sealed class DevAuthStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState DevState = new(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name,  "Dev Admin"),
            new Claim(ClaimTypes.Upn,   "dev-admin@raizen.local"),
            new Claim("preferred_username", "dev-admin@raizen.local"),
            new Claim(ClaimTypes.Role,  "Raizen.Admin"),
            new Claim(ClaimTypes.Role,  "Raizen.Approver"),
            new Claim(ClaimTypes.Role,  "Raizen.Operator"),
        ], authenticationType: "DevAuth")));

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(DevState);
}
