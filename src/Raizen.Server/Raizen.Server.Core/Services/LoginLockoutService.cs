using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;

namespace Raizen.Server.Core.Services;

/// <summary>
/// Tracks failed login attempts per IP with persistence across server restarts.
///
/// Architecture: ConcurrentDictionary as a read cache for hot-path performance
/// (checked on every login POST), backed by the login_lockouts PostgreSQL table
/// so lockouts survive service restarts.
/// </summary>
public sealed class LoginLockoutService(IDbContextFactory<RaizenDbContext> dbFactory) : ILoginLockoutService
{
    internal const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _cache = new();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Database
            .SqlQueryRaw<LockoutRow>(
                "SELECT ip AS \"Ip\", count AS \"Count\", locked_until AS \"LockedUntil\" FROM login_lockouts WHERE locked_until > now()")
            .ToListAsync(ct);

        foreach (var row in rows)
            _cache[row.Ip] = (row.Count, row.LockedUntil);
    }

    public bool IsLocked(string ip)
    {
        if (!_cache.TryGetValue(ip, out var entry)) return false;
        var now = DateTimeOffset.UtcNow;
        if (IsEntryLocked(entry, now)) return true;
        if (entry.Until <= now) _cache.TryRemove(ip, out _);
        return false;
    }

    public async Task<int> RecordFailureAsync(string ip, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var newEntry = _cache.AddOrUpdate(ip,
            _ => NextFailure(null, now),
            (_, old) => NextFailure(old, now));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO login_lockouts (ip, count, locked_until) VALUES ({ip}, {newEntry.Count}, {newEntry.Until}) ON CONFLICT (ip) DO UPDATE SET count = EXCLUDED.count, locked_until = EXCLUDED.locked_until",
            ct);

        return newEntry.Count;
    }

    public async Task ClearAsync(string ip, CancellationToken ct = default)
    {
        _cache.TryRemove(ip, out _);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM login_lockouts WHERE ip = {ip}", ct);
    }

    public async Task CleanupExpiredAsync(CancellationToken ct = default)
    {
        // Remove expired entries from cache
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Until <= now)
                _cache.TryRemove(key, out _);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM login_lockouts WHERE locked_until <= now()", ct);
    }

    // Helper record for SqlQueryRaw mapping
    private record LockoutRow(string Ip, int Count, DateTimeOffset LockedUntil);

    internal static bool IsEntryLocked((int Count, DateTimeOffset Until) entry, DateTimeOffset now) =>
        entry.Count >= MaxAttempts && now < entry.Until;

    internal static (int Count, DateTimeOffset Until) NextFailure(
        (int Count, DateTimeOffset Until)? current,
        DateTimeOffset now)
    {
        var count = current is { } entry && entry.Until > now
            ? entry.Count + 1
            : 1;

        return (count, now.Add(LockoutDuration));
    }
}
