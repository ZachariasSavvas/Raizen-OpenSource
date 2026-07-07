using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Raizen.Server.Core.Services;

/// <summary>
/// Background service that periodically:
///   1. Expires stale Pending/Approved requests.
///   2. Auto-disables endpoints that have not checked in for longer than DormantDays.
/// </summary>
public sealed class ExpiryBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILoginLockoutService lockoutService,
    ILogger<ExpiryBackgroundService> logger) : BackgroundService
{
    private const int DormantDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Expiry background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                using var scope = scopeFactory.CreateScope();

                var requestSvc = scope.ServiceProvider.GetRequiredService<IRequestService>();
                await requestSvc.ExpireStaleRequestsAsync(stoppingToken);

                var endpointSvc = scope.ServiceProvider.GetRequiredService<IEndpointService>();
                await endpointSvc.DisableDormantAsync(DormantDays, stoppingToken);

                await endpointSvc.CleanupExpiredPreviousKeysAsync(stoppingToken);
                await lockoutService.CleanupExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in expiry background service.");
            }
        }
    }
}
