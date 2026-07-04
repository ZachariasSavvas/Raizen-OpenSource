namespace Raizen.Server.Core.Licensing;

public interface ILicenseService
{
    LicenseInfo? License { get; }

    /// <summary>True when a license file was found, signature is valid, and it is not expired.</summary>
    bool IsValid { get; }

    /// <summary>True when the server's hardware fingerprint matches one in the license AllowedFingerprints list.</summary>
    bool IsHardwareValid { get; }

    /// <summary>Number of active endpoints (checked in within DormantDays). Refreshed every 5 min.</summary>
    int ActiveEndpoints { get; }

    /// <summary>Number of enabled endpoints that have not checked in within DormantDays.</summary>
    int DormantEndpoints { get; }

    /// <summary>MaxEndpoints from the license, or TrialMaxEndpoints when running without a valid license.</summary>
    int MaxEndpoints { get; }

    /// <summary>Seats remaining before the grace buffer kicks in.</summary>
    int AvailableSeats { get; }

    /// <summary>True while ActiveEndpoints is below the hard cap (MaxEndpoints + GracePercent%).</summary>
    bool HasAvailableSeats { get; }

    /// <summary>True when the server is running on a previously-valid license that has since expired/gone missing, within the 7-day grace window.</summary>
    bool IsInGracePeriod { get; }

    /// <summary>When the 7-day grace period ends. Null if not in grace.</summary>
    DateTimeOffset? GraceExpiresAt { get; }

    /// <summary>Configured dormant threshold in days.</summary>
    int DormantDays { get; }

    /// <summary>Parsed license tier. Trial when no valid license.</summary>
    LicenseTier Tier { get; }

    /// <summary>Returns true if the current license tier includes the given feature.</summary>
    bool HasFeature(LicenseFeature feature);

    /// <summary>Human-readable one-liner for logs and UI.</summary>
    string StatusSummary { get; }

    /// <summary>The path where the license file was found (or the expected path if missing).</summary>
    string LicenseFilePath { get; }

    /// <summary>Called from Program.cs after DI is built. Loads the license and initialises the seat count.</summary>
    Task InitializeAsync();

    /// <summary>Queries the DB for the current active/dormant endpoint counts.</summary>
    Task RefreshSeatCountAsync();

    /// <summary>
    /// Checks whether a seat is available and, if so, optimistically increments the in-memory count.
    /// Returns false if the hard cap (MaxEndpoints × (1 + GracePercent/100)) is reached.
    /// The next DB refresh will correct the count if the registration ultimately fails.
    /// </summary>
    bool TryConsumeSeat();

    /// <summary>Returns the hardware fingerprint (SHA-256 hex) of the current server.</summary>
    string GetHardwareFingerprint();
}
