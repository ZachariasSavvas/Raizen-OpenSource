using Npgsql;
using Raizen.Server.Core.Licensing;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Web.Services;

/// <summary>
/// Background service that listens for PostgreSQL NOTIFY events on the
/// 'raizen_new_request' channel and fans them out to connected Blazor circuits
/// via <see cref="IApprovalToastNotifier"/>.
/// </summary>
public sealed class PgNotifyListenerService : BackgroundService
{
    private readonly IApprovalToastNotifier _notifier;
    private readonly ILicenseService _license;
    private readonly string _connectionString;
    private readonly ILogger<PgNotifyListenerService> _logger;

    public PgNotifyListenerService(
        IApprovalToastNotifier notifier,
        ILicenseService license,
        IConfiguration config,
        ILogger<PgNotifyListenerService> logger)
    {
        _notifier         = notifier;
        _license          = license;
        _connectionString = config.GetConnectionString("Default")!;
        _logger           = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for app startup to complete
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PgNotifyListener connection lost. Reconnecting in 5 seconds.");
                try { await Task.Delay(5000, stoppingToken); } catch { break; }
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        conn.Notification += (_, args) =>
        {
            if (!_license.HasFeature(LicenseFeature.RealtimeToasts))
                return;

            if (Guid.TryParse(args.Payload, out var requestId))
            {
                _notifier.NotifyNewRequest(requestId);
            }
        };

        await using (var cmd = new NpgsqlCommand("LISTEN raizen_new_request", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("PgNotifyListener connected and listening on 'raizen_new_request'.");

        // Wait for notifications indefinitely
        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);
        }
    }
}
