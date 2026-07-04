using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Runs once on startup. If the config has a RegistrationToken but no ApiKey,
/// exchanges the token for a permanent ApiKey and writes it to raizen-config.json.
/// Subsequent service starts skip this entirely.
/// </summary>
public sealed class RegistrationWorker(
    ConfigLoader configLoader,
    IHttpClientFactory httpFactory,
    ILogger<RegistrationWorker> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = configLoader.Current;

        // Nothing to do — already registered
        if (string.IsNullOrEmpty(config.RegistrationToken))
            return;

        // Both set: ApiKey takes precedence, clear the stale token
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            log.LogInformation("ApiKey already present; clearing stale RegistrationToken.");
            config.RegistrationToken = null;
            configLoader.Save(config);
            return;
        }

        log.LogInformation("RegistrationToken found — exchanging for permanent ApiKey...");

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await ExchangeAsync(config, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 5)
            {
                var delay = TimeSpan.FromSeconds(attempt * 10);
                log.LogWarning(ex,
                    "Token exchange attempt {Attempt}/5 failed. Retrying in {Delay}s.", attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex,
                    "Token exchange failed after 5 attempts. Service will start without an ApiKey — " +
                    "polling is skipped until the config is corrected.");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ExchangeAsync(RaizenEndpointConfig config, CancellationToken ct)
    {
        var dto = new ExchangeTokenDto
        {
            RegistrationToken = config.RegistrationToken!,
            MachineName       = Environment.MachineName,
            OsVersion         = Environment.OSVersion.VersionString,
            AgentVersion      = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
        };

        var client = httpFactory.CreateClient("Raizen");
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Remove("X-Raizen-MachineId");
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);

        var response = await client.PostAsJsonAsync("api/v1/endpoints/exchange-token", dto, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Exchange returned HTTP {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<ExchangeTokenResponseDto>(ct)
                     ?? throw new InvalidOperationException("Empty response from exchange endpoint.");

        // Persist ApiKey, clear RegistrationToken — MachineId is already populated by ConfigLoader
        config.ApiKey            = result.ApiKey;
        config.RegistrationToken = null;
        configLoader.Save(config);

        log.LogInformation(
            "Registration complete. Endpoint registered with Id={RegistrationId}.",
            result.RegistrationId);
    }
}
