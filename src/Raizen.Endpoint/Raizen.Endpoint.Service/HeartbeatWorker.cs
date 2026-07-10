using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Sends periodic heartbeats so the server knows the endpoint is alive
/// and can display the agent version and OS version in the admin UI.
/// </summary>
public sealed class HeartbeatWorker(
    ConfigLoader configLoader,
    IHttpClientFactory httpFactory,
    EndpointHealthCollector healthCollector,
    AgentHealthState health,
    ILogger<HeartbeatWorker> log) : BackgroundService
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = configLoader.Current;
            var interval = TimeSpan.FromSeconds(Math.Max(60, config.HeartbeatIntervalSeconds));

            try
            {
                await SendHeartbeatAsync(config, stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Heartbeat failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task SendHeartbeatAsync(RaizenEndpointConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ServerUrl)) return;

        var snapshot = health.Current;
        var dto = new EndpointHeartbeatDto
        {
            AgentVersion = AgentVersion,
            OsVersion = Environment.OSVersion.VersionString,
            Timestamp = DateTimeOffset.UtcNow,
            PollSigningConfigured = !string.IsNullOrWhiteSpace(config.ServerPublicKeyPem),
            LastPollSucceededAt = snapshot.LastPollSucceededAt,
            LastPollError = snapshot.LastPollError,
            LastUpdateCheckAt = snapshot.LastUpdateCheckAt,
            LastUpdateStatus = snapshot.LastUpdateStatus,
            LastUpdateError = snapshot.LastUpdateError,
            LastSuccessfulUpdateAt = snapshot.LastSuccessfulUpdateAt,
            Health = healthCollector.Collect(),
        };

        using var client = BuildClient(config);
        var resp = await client.PostAsJsonAsync("api/v1/endpoints/heartbeat", dto, ct);
        if (!resp.IsSuccessStatusCode)
            log.LogWarning("Heartbeat returned {Status}", resp.StatusCode);
    }

    private HttpClient BuildClient(RaizenEndpointConfig config)
    {
        var client = httpFactory.CreateClient("Raizen");
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);
        client.DefaultRequestHeaders.Add("X-Raizen-ApiKey", config.ApiKey);
        return client;
    }
}
