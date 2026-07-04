using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Periodically checks the server for a newer agent version and self-updates
/// by downloading the MSI and running msiexec silently.
///
/// The MSI handles stopping the service, replacing the binaries, and restarting —
/// so this worker does not need to coordinate shutdown.
///
/// The actual update logic lives in <see cref="AgentUpdateService"/> so that
/// <see cref="ElevationWorker"/> can also trigger an immediate update when
/// the server signals one via <c>SignedPollResponseDto.UpdateNow</c>.
/// </summary>
public sealed class UpdateWorker(
    AgentUpdateService updateService,
    ILogger<UpdateWorker> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    // Wait after startup so RegistrationWorker can complete before we try to call the server.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("UpdateWorker started.");

        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await updateService.CheckAndUpdateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Update check failed.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
