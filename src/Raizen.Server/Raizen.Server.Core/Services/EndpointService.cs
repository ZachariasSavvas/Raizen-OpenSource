using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IEndpointService
{
    /// <summary>
    /// Validates the provided API key against the stored hash.
    /// Returns the registration record on success, null on failure.
    /// Admin-disabled endpoints (DormantSince = null) are blocked; auto-disabled endpoints
    /// (DormantSince set) are allowed through so they can re-enable on heartbeat.
    /// </summary>
    Task<EndpointRegistration?> AuthenticateAsync(string machineId, string apiKey, CancellationToken ct = default);

    Task<EndpointRegistrationDto> RegisterAsync(string machineId, string apiKey, RegisterEndpointDto dto, CancellationToken ct = default);
    Task HeartbeatAsync(Guid registrationId, EndpointHeartbeatDto dto, CancellationToken ct = default);
    Task<List<EndpointRegistrationDto>> ListAsync(CancellationToken ct = default);
    Task SetEnabledAsync(Guid id, bool enabled, string actorUpn, CancellationToken ct = default);

    /// <summary>
    /// Finds all enabled endpoints that have not checked in for longer than <paramref name="dormantDays"/>
    /// and auto-disables them (sets IsEnabled=false, DormantSince=now).
    /// </summary>
    Task DisableDormantAsync(int dormantDays, CancellationToken ct = default);

    /// <summary>
    /// Generates a new API key for the endpoint, updates the stored hash, and returns the
    /// plaintext key (shown once — caller must display it to the admin immediately).
    /// </summary>
    Task<string> RotateApiKeyAsync(Guid id, string actorUpn, CancellationToken ct = default);

    /// <summary>Generates a new random API key. Caller must securely deliver it to the endpoint.</summary>
    static string GenerateApiKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    static string HashApiKey(string apiKey)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Creates or updates an endpoint registration and returns the record.
    /// Used by RegistrationTokenService so the upsert logic is not duplicated.
    /// </summary>
    Task<EndpointRegistration> UpsertAsync(
        string machineId, string newApiKey, RegisterEndpointDto dto, CancellationToken ct = default);

    Task<EndpointRegistrationDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Flags the specified endpoints (or all enabled endpoints if <paramref name="registrationIds"/> is null/empty)
    /// to receive an immediate update on their next poll cycle.
    /// </summary>
    Task PushUpdateAsync(IList<Guid>? registrationIds, string actorUpn, CancellationToken ct = default);

    /// <summary>Clears the UpdatePending flag after the endpoint has been served the update signal.</summary>
    Task AcknowledgeUpdateAsync(Guid registrationId, CancellationToken ct = default);

    /// <summary>Clears expired previous API key hashes after the grace period ends.</summary>
    Task CleanupExpiredPreviousKeysAsync(CancellationToken ct = default);
}

public sealed class EndpointService(IDbContextFactory<RaizenDbContext> dbFactory, IAuditService audit) : IEndpointService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<EndpointRegistration?> AuthenticateAsync(
        string machineId, string apiKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hash = IEndpointService.HashApiKey(apiKey);
        var reg = await db.EndpointRegistrations
            .FirstOrDefaultAsync(e => e.MachineId == machineId, ct);

        // Admin-disabled endpoints (IsEnabled=false AND DormantSince=null) are truly blocked.
        // Auto-disabled endpoints (DormantSince set) are allowed through so HeartbeatAsync
        // can re-enable them when the machine comes back online.
        var effectiveReg = (reg is null || (!reg.IsEnabled && reg.DormantSince is null))
            ? null : reg;

        // Always call FixedTimeEquals even when reg is null so that the response
        // time is independent of whether the machine ID is registered, preventing
        // timing-based enumeration of valid machine IDs.
        var storedHash = effectiveReg?.ApiKeyHash ?? new string('0', hash.Length);
        var hashBytes = Encoding.UTF8.GetBytes(hash);
        var storedBytes = Encoding.UTF8.GetBytes(storedHash);

        if (CryptographicOperations.FixedTimeEquals(storedBytes, hashBytes))
            return effectiveReg;

        // Check previous key if within grace period
        if (effectiveReg?.PreviousApiKeyHash != null
            && effectiveReg.PreviousKeyExpiresAt > DateTimeOffset.UtcNow)
        {
            var prevBytes = Encoding.UTF8.GetBytes(effectiveReg.PreviousApiKeyHash);
            if (CryptographicOperations.FixedTimeEquals(prevBytes, hashBytes))
                return effectiveReg;
        }

        return null;
    }

    public async Task<EndpointRegistrationDto> RegisterAsync(
        string machineId, string apiKey, RegisterEndpointDto dto, CancellationToken ct = default)
    {
        var reg = await UpsertAsync(machineId, apiKey, dto, ct);
        await audit.LogAsync("endpoint.registered", $"machine:{machineId}",
            targetMachine: dto.MachineName, ct: ct);
        return Map(reg);
    }

    public async Task HeartbeatAsync(Guid registrationId, EndpointHeartbeatDto dto, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reg = await db.EndpointRegistrations.FindAsync([registrationId], ct);
        if (reg is null) return;

        var wasAutoDisabled = reg.DormantSince is not null;

        reg.LastSeenAt   = DateTimeOffset.UtcNow;
        reg.AgentVersion = dto.AgentVersion;
        reg.OsVersion    = dto.OsVersion;
        reg.PollSigningConfigured = dto.PollSigningConfigured;
        reg.LastPollSucceededAt = dto.LastPollSucceededAt;
        reg.LastPollError = TrimHealth(dto.LastPollError);
        reg.LastUpdateCheckAt = dto.LastUpdateCheckAt;
        reg.LastUpdateStatus = TrimHealth(dto.LastUpdateStatus, 64);
        reg.LastUpdateError = TrimHealth(dto.LastUpdateError);
        reg.LastSuccessfulUpdateAt = dto.LastSuccessfulUpdateAt;

        if (wasAutoDisabled)
        {
            reg.IsEnabled    = true;
            reg.DormantSince = null;
        }

        await db.SaveChangesAsync(ct);

        if (wasAutoDisabled)
            await audit.LogAsync("endpoint.reactivated", "system",
                targetMachine: reg.MachineName, ct: ct);
    }

    public async Task<List<EndpointRegistrationDto>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.EndpointRegistrations.OrderBy(e => e.MachineName).ToListAsync(ct);
        return all.Select(Map).ToList();
    }

    public async Task<EndpointRegistration> UpsertAsync(
        string machineId, string newApiKey, RegisterEndpointDto dto, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.EndpointRegistrations
            .FirstOrDefaultAsync(e => e.MachineId == machineId, ct);

        var hash = IEndpointService.HashApiKey(newApiKey);

        if (existing is not null)
        {
            // Only re-check the seat limit if this endpoint was dormant (no check-in for > DormantDays).
            // Active endpoints that are simply re-registering/updating do not consume an additional seat.
            existing.ApiKeyHash   = hash;
            existing.MachineName  = dto.MachineName;
            existing.OsVersion    = dto.OsVersion;
            existing.AgentVersion = dto.AgentVersion;
            existing.LastSeenAt   = DateTimeOffset.UtcNow;
        }
        else
        {
            // New endpoint registration — must consume a seat.
            existing = new EndpointRegistration
            {
                MachineId    = machineId,
                MachineName  = dto.MachineName,
                ApiKeyHash   = hash,
                OsVersion    = dto.OsVersion,
                AgentVersion = dto.AgentVersion,
            };
            db.EndpointRegistrations.Add(existing);
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reg = await db.EndpointRegistrations.FindAsync([id], ct)
            ?? throw new InvalidOperationException("Endpoint not found.");
        reg.IsEnabled    = enabled;
        reg.DormantSince = null; // clear auto-disable flag — this is now an explicit admin action
        await db.SaveChangesAsync(ct);
        var evt = enabled ? "endpoint.enabled" : "endpoint.disabled";
        await audit.LogAsync(evt, actorUpn, targetMachine: reg.MachineName, ct: ct);
    }

    public async Task DisableDormantAsync(int dormantDays, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var threshold = DateTimeOffset.UtcNow.AddDays(-dormantDays);

        var dormant = await db.EndpointRegistrations
            .Where(e => e.IsEnabled
                     && e.DormantSince == null
                     && (e.LastSeenAt == null || e.LastSeenAt < threshold))
            .ToListAsync(ct);

        if (dormant.Count == 0) return;

        foreach (var reg in dormant)
        {
            reg.IsEnabled    = false;
            reg.DormantSince = DateTimeOffset.UtcNow;
            await audit.LogAsync("endpoint.auto-disabled", "system",
                targetMachine: reg.MachineName,
                detail: $"No check-in for over {dormantDays} days",
                ct: ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RotateApiKeyAsync(Guid id, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reg = await db.EndpointRegistrations.FindAsync([id], ct)
            ?? throw new InvalidOperationException("Endpoint not found.");
        var newKey = IEndpointService.GenerateApiKey();
        // Preserve old key with 4-hour grace period
        reg.PreviousApiKeyHash = reg.ApiKeyHash;
        reg.PreviousKeyExpiresAt = DateTimeOffset.UtcNow.AddHours(4);
        reg.ApiKeyHash = IEndpointService.HashApiKey(newKey);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("endpoint.key-rotated", actorUpn, targetMachine: reg.MachineName, ct: ct);
        return newKey;
    }

    public async Task<EndpointRegistrationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reg = await db.EndpointRegistrations.FindAsync([id], ct);
        return reg is null ? null : Map(reg);
    }

    public async Task PushUpdateAsync(IList<Guid>? registrationIds, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        List<EndpointRegistration> targets;

        if (registrationIds is { Count: > 0 })
        {
            targets = await db.EndpointRegistrations
                .Where(e => registrationIds.Contains(e.Id))
                .ToListAsync(ct);
        }
        else
        {
            targets = await db.EndpointRegistrations
                .Where(e => e.IsEnabled)
                .ToListAsync(ct);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var reg in targets)
        {
            reg.UpdatePending      = true;
            reg.UpdateRequestedAt  = now;
        }

        await db.SaveChangesAsync(ct);

        var names = string.Join(", ", targets.Select(r => r.MachineName));
        await audit.LogAsync("endpoint.update-pushed", actorUpn,
            detail: $"Update push queued for {targets.Count} endpoint(s): {names}", ct: ct);
    }

    public async Task AcknowledgeUpdateAsync(Guid registrationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reg = await db.EndpointRegistrations.FindAsync([registrationId], ct);
        if (reg is null) return;
        reg.UpdatePending     = false;
        reg.UpdateRequestedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task CleanupExpiredPreviousKeysAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.EndpointRegistrations
            .Where(e => e.PreviousKeyExpiresAt != null && e.PreviousKeyExpiresAt < DateTimeOffset.UtcNow)
            .ToListAsync(ct);
        foreach (var ep in expired)
        {
            ep.PreviousApiKeyHash = null;
            ep.PreviousKeyExpiresAt = null;
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
    }

    private static EndpointRegistrationDto Map(EndpointRegistration e)
    {
        var tags = string.IsNullOrEmpty(e.TagsJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(e.TagsJson) ?? [];
        return new EndpointRegistrationDto
        {
            Id = e.Id,
            MachineId = e.MachineId,
            MachineName = e.MachineName,
            Description = e.Description,
            IsEnabled = e.IsEnabled,
            RegisteredAt = e.RegisteredAt,
            LastSeenAt = e.LastSeenAt,
            OsVersion = e.OsVersion,
            AgentVersion = e.AgentVersion,
            Tags = tags,
            DormantSince = e.DormantSince,
            UpdatePending = e.UpdatePending,
            UpdateRequestedAt = e.UpdateRequestedAt,
            PollSigningConfigured = e.PollSigningConfigured,
            LastPollSucceededAt = e.LastPollSucceededAt,
            LastPollError = e.LastPollError,
            LastUpdateCheckAt = e.LastUpdateCheckAt,
            LastUpdateStatus = e.LastUpdateStatus,
            LastUpdateError = e.LastUpdateError,
            LastSuccessfulUpdateAt = e.LastSuccessfulUpdateAt,
        };
    }

    private static string? TrimHealth(string? value, int max = 1000)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}
