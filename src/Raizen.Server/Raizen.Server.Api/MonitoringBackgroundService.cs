using Raizen.Server.Core.Services;

namespace Raizen.Server.Api;

public sealed class MonitoringBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<MonitoringBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var monitoring = scope.ServiceProvider.GetRequiredService<IMonitoringService>();
                await monitoring.EvaluateAllAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<IDiagnosticBundleService>()
                    .DeleteExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Monitoring evaluation failed."); }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
