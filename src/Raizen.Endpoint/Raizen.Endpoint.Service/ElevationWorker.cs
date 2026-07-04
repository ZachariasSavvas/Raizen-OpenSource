using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Service.Actions;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Core background worker that polls the Raizen server for approved requests
/// and dispatches them to the appropriate <see cref="IActionHandler"/>.
///
/// Communication flow:
///   1. Poll  GET  /api/v1/requests/pending-execution
///   2. For each approved request: mark Executing (POST /api/v1/requests/{id}/executing)
///   3. Dispatch to IActionHandler
///   4. Report result  POST /api/v1/requests/execution-result
/// </summary>
public sealed class ElevationWorker(
    ConfigLoader configLoader,
    ActionHandlerRegistry registry,
    IHttpClientFactory httpFactory,
    OfflineQueueService offlineQueue,
    AgentUpdateService updateService,
    AgentHealthState health,
    ILogger<ElevationWorker> log) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Raizen elevation worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = configLoader.Current;
            var interval = TimeSpan.FromSeconds(Math.Max(5, config.PollIntervalSeconds));

            try
            {
                await PollAndExecuteAsync(config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error in elevation poll cycle.");
            }

            await Task.Delay(interval, stoppingToken);
        }

        log.LogInformation("Raizen elevation worker stopped.");
    }

    private async Task PollAndExecuteAsync(RaizenEndpointConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ServerUrl))
        {
            log.LogWarning("Server URL or API key is not configured. Skipping poll.");
            health.PollFailed("Server URL or API key is not configured.");
            return;
        }

        using var client = BuildClient(config);

        // ── Flush any results queued while server was unreachable ─────────
        await FlushOfflineQueueAsync(client, ct);

        List<ElevationRequestDto> pending;
        try
        {
            var response = await client.GetAsync("api/v1/requests/pending-execution", ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Poll returned {Status}", response.StatusCode);
                health.PollFailed($"Poll returned {response.StatusCode}.");
                return;
            }

            var signed = await response.Content.ReadFromJsonAsync<SignedPollResponseDto>(JsonOpts, ct);
            if (signed is null)
            {
                log.LogWarning("Poll returned a null or unparseable response body.");
                health.PollFailed("Poll returned a null or unparseable response body.");
                return;
            }

            if (!PollSignatureVerifier.Verify(signed, config.ServerPublicKeyPem, log))
            {
                health.PollFailed("Poll response signature verification failed.");
                return;
            }

            pending = PollSignatureVerifier.ParseRequests(signed, JsonOpts);
            health.PollSucceeded();

            if (signed.UpdateNow)
            {
                log.LogInformation("Server signalled immediate agent update. Triggering update check.");
                _ = Task.Run(() => updateService.CheckAndUpdateAsync(ct), ct);
            }
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Could not reach Raizen server at {Url}.", config.ServerUrl);
            health.PollFailed($"Could not reach server: {ex.Message}");
            return;
        }

        foreach (var request in pending)
        {
            if (ct.IsCancellationRequested) break;
            await ExecuteRequestAsync(client, config, request, ct);
        }
    }

    private async Task FlushOfflineQueueAsync(HttpClient client, CancellationToken ct)
    {
        if (!offlineQueue.HasItems()) return;

        var items = offlineQueue.DequeueAll();
        log.LogInformation("Flushing {Count} offline-queued result(s) to server.", items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var item = items[i];
            var dto  = new ExecutionResultDto
            {
                RequestId     = item.RequestId,
                Succeeded     = item.Succeeded,
                ResultMessage = item.ResultMessage,
                ErrorMessage  = item.ErrorMessage,
                ExecutedAt    = item.ExecutedAt,
            };

            try
            {
                await client.PostAsJsonAsync("api/v1/requests/execution-result", dto, JsonOpts, ct);
            }
            catch (HttpRequestException ex)
            {
                // Server still unreachable — re-queue this item and all remaining ones
                var remaining = items.Skip(i).ToList();
                log.LogWarning(ex, "Server still unreachable; re-queuing {Count} offline result(s).", remaining.Count);
                foreach (var r in remaining)
                    offlineQueue.Enqueue(r.RequestId,
                        new ActionResult(r.Succeeded, r.ResultMessage, r.ErrorMessage));
                return;
            }
        }
    }

    private async Task ExecuteRequestAsync(
        HttpClient client,
        RaizenEndpointConfig config,
        ElevationRequestDto request,
        CancellationToken ct)
    {
        log.LogInformation(
            "Executing request {RequestId}: {Action} for {Requester}",
            request.Id, request.ActionDisplayName, request.RequesterUpn);

        // Mark as Executing to prevent duplicate dispatch
        var markResp = await client.PostAsync(
            $"api/v1/requests/{request.Id}/executing",
            null, ct);

        if (!markResp.IsSuccessStatusCode)
        {
            log.LogWarning("Could not mark request {Id} as executing: {Status}",
                request.Id, markResp.StatusCode);
            return;
        }

        // Find the handler
        var handler = registry.GetHandler(request.ActionType);
        if (handler is null)
        {
            log.LogError("No handler registered for action type {Type}.", request.ActionType);
            await ReportResultAsync(client, request.Id,
                new ActionResult(false, ErrorMessage: $"No handler for action type {request.ActionType}."), ct);
            return;
        }

        // Pre-flight check (optional — only handlers that implement IPreflightCheck)
        if (handler is IPreflightCheck check)
        {
            var preflightError = check.Validate(request);
            if (preflightError is not null)
            {
                log.LogWarning("Pre-flight check failed for request {Id}: {Error}", request.Id, preflightError);
                await ReportResultAsync(client, request.Id,
                    new ActionResult(false, ErrorMessage: $"Pre-flight check failed: {preflightError}"), ct);
                return;
            }
        }

        // Execute
        ActionResult result;
        try
        {
            result = await handler.ExecuteAsync(request, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled exception in action handler {Type}.", request.ActionType);
            result = new ActionResult(false, ErrorMessage: $"Unhandled exception: {ex.Message}");
        }

        log.LogInformation(
            "Request {Id} completed: Succeeded={Success} Message={Msg}",
            request.Id, result.Succeeded, result.ResultMessage ?? result.ErrorMessage);

        await ReportResultAsync(client, request.Id, result, ct);
    }

    private async Task ReportResultAsync(
        HttpClient client, Guid requestId, ActionResult result, CancellationToken ct)
    {
        var dto = new ExecutionResultDto
        {
            RequestId     = requestId,
            Succeeded     = result.Succeeded,
            ResultMessage = result.ResultMessage,
            ErrorMessage  = result.ErrorMessage,
            ExecutedAt    = DateTimeOffset.UtcNow,
        };

        try
        {
            await client.PostAsJsonAsync("api/v1/requests/execution-result", dto, JsonOpts, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex,
                "Could not report result for request {RequestId}; queuing for next cycle.", requestId);
            offlineQueue.Enqueue(requestId, result);
        }
    }

    private HttpClient BuildClient(RaizenEndpointConfig config)
    {
        var client = httpFactory.CreateClient("Raizen");
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);
        client.DefaultRequestHeaders.Add("X-Raizen-ApiKey", config.ApiKey);
        client.Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds);
        return client;
    }
}
