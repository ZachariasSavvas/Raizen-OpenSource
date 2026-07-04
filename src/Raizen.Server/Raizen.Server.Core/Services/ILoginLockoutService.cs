namespace Raizen.Server.Core.Services;

public interface ILoginLockoutService
{
    /// <summary>Load persisted lockouts from DB into the in-memory cache. Call once at startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Returns true if the IP is currently locked out.</summary>
    bool IsLocked(string ip);

    /// <summary>Records a failed attempt. Returns the new failure count.</summary>
    Task<int> RecordFailureAsync(string ip, CancellationToken ct = default);

    /// <summary>Clears the lockout for the given IP (call on successful login).</summary>
    Task ClearAsync(string ip, CancellationToken ct = default);

    /// <summary>Deletes expired rows from the DB. Called periodically by ExpiryBackgroundService.</summary>
    Task CleanupExpiredAsync(CancellationToken ct = default);
}
