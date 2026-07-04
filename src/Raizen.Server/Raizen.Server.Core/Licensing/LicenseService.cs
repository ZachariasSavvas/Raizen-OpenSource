using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raizen.Server.Core.Data;

namespace Raizen.Server.Core.Licensing;

/// <summary>
/// Open-source entitlement service. All product features are enabled and endpoint
/// registration is unrestricted; the remaining counters support health/status UI.
/// </summary>
public sealed class LicenseService(
    IServiceScopeFactory scopeFactory,
    ILogger<LicenseService> logger) : ILicenseService
{
    public LicenseInfo? License => null;
    public bool IsValid => true;
    public bool IsHardwareValid => true;
    public bool IsInGracePeriod => false;
    public DateTimeOffset? GraceExpiresAt => null;
    public int ActiveEndpoints { get; private set; }
    public int DormantEndpoints { get; private set; }
    public int DormantDays => 30;
    public int MaxEndpoints => int.MaxValue;
    public int AvailableSeats => int.MaxValue;
    public bool HasAvailableSeats => true;
    public LicenseTier Tier => LicenseTier.Enterprise;
    public string StatusSummary => "Open-source edition: all features enabled.";
    public string LicenseFilePath => string.Empty;

    public Task InitializeAsync() => RefreshSeatCountAsync();

    public async Task RefreshSeatCountAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
            var threshold = DateTimeOffset.UtcNow.AddDays(-DormantDays);

            ActiveEndpoints = await db.EndpointRegistrations
                .CountAsync(e => e.IsEnabled && e.LastSeenAt >= threshold);
            DormantEndpoints = await db.EndpointRegistrations
                .CountAsync(e => e.IsEnabled && (e.LastSeenAt == null || e.LastSeenAt < threshold));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Endpoint entitlement counters could not be refreshed.");
        }
    }

    public bool TryConsumeSeat() => true;

    public string GetHardwareFingerprint() => HardwareFingerprint.Get();

    public bool HasFeature(LicenseFeature feature) => true;

    internal static LicenseTier ParseTier(string? tierString) => LicenseTier.Enterprise;
}
