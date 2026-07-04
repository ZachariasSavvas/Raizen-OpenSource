using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Raizen.Server.Api.Auth;

/// <summary>
/// Development-only authentication scheme.
/// Accepts the header  X-Raizen-Dev-Token: &lt;token&gt;  and grants the caller
/// the Raizen.Admin role so admin/approver API endpoints can be tested without
/// a real Entra ID setup.
///
/// Only active when  DevMode:Enabled = true  in appsettings.Development.json.
/// NEVER register this scheme in Production.
/// </summary>
public sealed class DevAdminAuthOptions : AuthenticationSchemeOptions
{
    public string DevToken { get; set; } = "dev-admin-token";
}

public sealed class DevAdminAuthHandler(
    IOptionsMonitor<DevAdminAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<DevAdminAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Raizen-Dev-Token", out var tokenValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (tokenValues.ToString() != Options.DevToken)
            return Task.FromResult(AuthenticateResult.Fail("Invalid dev token."));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "dev-admin"),
            new Claim("preferred_username", "dev-admin@localhost"),
            new Claim(ClaimTypes.Role, "Raizen.Admin"),
            new Claim(ClaimTypes.Role, "Raizen.Approver"),
        };

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
