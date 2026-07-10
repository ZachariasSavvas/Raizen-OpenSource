using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;

namespace Raizen.Server.Core.Services;

public interface IAuditService
{
    Task LogAsync(
        string eventName,
        string actorUpn,
        Guid? requestId = null,
        string? targetMachine = null,
        string? detail = null,
        CancellationToken ct = default,
        string? ipAddress = null);

    /// <summary>
    /// Walks every audit log entry in chronological order and verifies the HMAC hash chain.
    /// Returns (Total entries checked, Number of entries with invalid/broken hashes).
    /// Entries predating the hash chain feature (RowHash is null) are counted but not validated.
    /// </summary>
    Task<(int Total, int Invalid)> VerifyChainAsync(CancellationToken ct = default);

    /// <summary>
    /// Recomputes RowHash and PreviousHash for every hashed audit log entry using the data
    /// as stored in the database, rebuilding a fully valid chain. Appends a chain.repaired
    /// audit entry documenting the repair. Returns the number of entries that were corrected.
    /// </summary>
    Task<int> RepairChainAsync(string actorUpn, CancellationToken ct = default);
}

public sealed class AuditService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    ISyslogSender syslog,
    IConfiguration config) : IAuditService
{
    private const long AuditChainLockId = 7_249_316_012;

    // The static lock serializes scopes inside one process. PostgreSQL writes also take
    // AuditChainLockId transactionally so API, Web, and multi-instance deployments cannot
    // read the same chain tip and create a fork.
    private static readonly SemaphoreSlim _chainLock = new(1, 1);

    public async Task LogAsync(
        string eventName,
        string actorUpn,
        Guid? requestId = null,
        string? targetMachine = null,
        string? detail = null,
        CancellationToken ct = default,
        string? ipAddress = null)
    {
        // Truncate to microsecond precision to match PostgreSQL timestamptz storage.
        // Without this, OccurredAt.ToString("O") differs between write-time (100ns ticks)
        // and read-back (μs precision), causing every HMAC to mismatch on verification.
        var now = new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond,
            TimeSpan.Zero);

        await _chainLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = db.Database.IsNpgsql()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (transaction is not null)
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AuditChainLockId})", ct);

            var prevHash = await db.AuditLogs
                .OrderByDescending(x => x.OccurredAt)
                .ThenByDescending(x => x.Id)
                .Select(x => x.RowHash)
                .FirstOrDefaultAsync(ct) ?? string.Empty;

            var entry = new AuditLog
            {
                Event         = eventName,
                ActorUpn      = actorUpn,
                RequestId     = requestId,
                TargetMachine = targetMachine,
                Detail        = detail,
                IpAddress     = ipAddress,
                OccurredAt    = now,
                PreviousHash  = prevHash,
            };
            entry.RowHash = ComputeHash(entry, HmacKey());

            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        finally
        {
            _chainLock.Release();
        }

        // Forward to SIEM — fire-and-forget, outside the lock
        syslog.Send(new SyslogPayload(eventName, actorUpn, targetMachine, detail, ipAddress, now));
    }

    public async Task<(int Total, int Invalid)> VerifyChainAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var key      = HmacKey();
        int total    = 0;
        int invalid  = 0;
        var prevHash = string.Empty;

        // Stream entries one at a time — avoids loading the entire audit log into memory
        await foreach (var entry in db.AuditLogs
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            total++;

            if (string.IsNullOrEmpty(entry.RowHash))
            {
                // Pre-dates the hash chain — skip but reset chain anchor
                prevHash = string.Empty;
                continue;
            }

            if (entry.PreviousHash != prevHash)
                invalid++;
            else if (entry.RowHash != ComputeHash(entry, key))
                invalid++;

            prevHash = entry.RowHash;
        }

        return (total, invalid);
    }

    public async Task<int> RepairChainAsync(string actorUpn, CancellationToken ct = default)
    {
        await _chainLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = db.Database.IsNpgsql()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (transaction is not null)
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AuditChainLockId})", ct);
                await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL raizen.audit_repair = 'on'", ct);
            }

            var key      = HmacKey();
            var prevHash = string.Empty;
            int repaired = 0;

            var entries = await db.AuditLogs
                .OrderBy(x => x.OccurredAt)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.RowHash))
                {
                    prevHash = string.Empty;
                    continue;
                }

                var needsPrevFix  = entry.PreviousHash != prevHash;
                entry.PreviousHash = prevHash;
                var correctHash   = ComputeHash(entry, key);
                var needsHashFix  = entry.RowHash != correctHash;

                if (needsPrevFix || needsHashFix)
                {
                    entry.RowHash = correctHash;
                    repaired++;
                }

                prevHash = entry.RowHash;
            }

            // Append a chain.repaired audit entry inside the lock so it becomes the new chain tip
            var now = new DateTimeOffset(
                DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond,
                TimeSpan.Zero);

            var repairEntry = new AuditLog
            {
                Event        = "chain.repaired",
                ActorUpn     = actorUpn,
                Detail       = $"{repaired} entries recomputed. Cause: timestamp precision mismatch in pre-fix entries.",
                OccurredAt   = now,
                PreviousHash = prevHash,
            };
            repairEntry.RowHash = ComputeHash(repairEntry, key);
            db.AuditLogs.Add(repairEntry);

            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            syslog.Send(new SyslogPayload("chain.repaired", actorUpn, null, repairEntry.Detail, null, now));
            return repaired;
        }
        finally
        {
            _chainLock.Release();
        }
    }

    // ── Hash helpers ────────────────────────────────────────────────────────

    private string HmacKey()
    {
        var key = config["Security:AuditHmacKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Security:AuditHmacKey is not configured. " +
                "Add a dedicated key separate from Security:EncryptionKey. " +
                "Run the setup wizard or add the key manually to appsettings.Production.json.");

        var encKey = config["Security:EncryptionKey"];
        if (encKey is not null && key == encKey)
            throw new InvalidOperationException(
                "Security:AuditHmacKey must not equal Security:EncryptionKey. " +
                "Use a separately generated random key for each.");

        return key;
    }

    public static string ComputeHash(AuditLog entry, string keyMaterial)
    {
        var data = string.Join("|",
            entry.Id, entry.Event, entry.ActorUpn,
            entry.TargetMachine ?? "", entry.Detail ?? "",
            entry.IpAddress ?? "", entry.OccurredAt.ToString("O"),
            entry.RequestId?.ToString() ?? "", entry.PreviousHash ?? "");

        byte[] keyBytes = string.IsNullOrEmpty(keyMaterial)
            ? new byte[32]
            : Encoding.UTF8.GetBytes(keyMaterial);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
