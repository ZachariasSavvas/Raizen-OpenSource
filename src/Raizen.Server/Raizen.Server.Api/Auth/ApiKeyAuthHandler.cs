using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Api.Auth;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authenticates endpoint agents via two HTTP headers:
///   X-Raizen-MachineId   — stable machine identity
///   X-Raizen-ApiKey      — secret key whose SHA-512 hash is stored in the DB
/// On success, a ClaimsPrincipal with role "Endpoint" is produced.
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<ApiKeyAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<ApiKeyAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string RoleEndpoint = "Endpoint";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Raizen-MachineId", out var machineIdValues) ||
            !Request.Headers.TryGetValue("X-Raizen-ApiKey", out var apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var machineId = machineIdValues.ToString();
        var apiKey = apiKeyValues.ToString();

        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(apiKey))
            return AuthenticateResult.Fail("Missing machine ID or API key.");

        EndpointRegistration? registration;
        using (var scope = scopeFactory.CreateScope())
        {
            var endpointService = scope.ServiceProvider.GetRequiredService<IEndpointService>();
            registration = await endpointService.AuthenticateAsync(machineId, apiKey, Context.RequestAborted);
        }

        if (registration is null)
            return AuthenticateResult.Fail("Invalid machine ID or API key.");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, registration.MachineName),
            new Claim("machine_id", registration.MachineId),
            new Claim("registration_id", registration.Id.ToString()),
            new Claim(ClaimTypes.Role, RoleEndpoint),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
