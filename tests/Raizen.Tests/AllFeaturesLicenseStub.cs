using Raizen.Server.Core.Licensing;

namespace Raizen.Tests;

/// <summary>
/// Stub ILicenseService that acts as a Business-tier license with all features enabled.
/// Used in tests where license gating is not the focus.
/// </summary>
internal sealed class AllFeaturesLicenseStub : ILicenseService
{
    public LicenseInfo? License => null;
    public bool IsValid => true;
    public bool IsHardwareValid => true;
    public int ActiveEndpoints => 0;
    public int DormantEndpoints => 0;
    public int MaxEndpoints => 999;
    public int AvailableSeats => 999;
    public bool HasAvailableSeats => true;
    public bool IsInGracePeriod => false;
    public DateTimeOffset? GraceExpiresAt => null;
    public int DormantDays => 30;
    public string StatusSummary => "Test";
    public string LicenseFilePath => "test.lic";
    public LicenseTier Tier => LicenseTier.Business;
    public bool HasFeature(LicenseFeature feature) => true;
    public Task InitializeAsync() => Task.CompletedTask;
    public Task RefreshSeatCountAsync() => Task.CompletedTask;
    public bool TryConsumeSeat() => true;
    public string GetHardwareFingerprint() => "test";
}
