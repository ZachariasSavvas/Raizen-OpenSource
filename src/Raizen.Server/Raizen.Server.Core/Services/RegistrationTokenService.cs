using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IRegistrationTokenService
{
    /// <summary>Creates a new registration token. Returns the plaintext token (shown once).</summary>
    Task<CreateRegistrationTokenResponseDto> CreateAsync(
        CreateRegistrationTokenDto dto, string createdBy, CancellationToken ct = default);

    /// <summary>
    /// Validates the plaintext token, atomically increments UseCount, upserts the
    /// EndpointRegistration, and returns the permanent ApiKey. Returns null on failure.
    /// </summary>
    Task<ExchangeTokenResponseDto?> ExchangeAsync(
        ExchangeTokenDto dto, string machineId, CancellationToken ct = default);

    Task<List<RegistrationTokenDto>> ListAsync(CancellationToken ct = default);
    Task RevokeAsync(Guid tokenId, string actorUpn, CancellationToken ct = default);
    Task DeleteAsync(Guid tokenId, string actorUpn, CancellationToken ct = default);
}

public sealed class RegistrationTokenService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    IEndpointService endpoints,
    IAuditService audit,
    ILogger<RegistrationTokenService> log) : IRegistrationTokenService
{
    public async Task<CreateRegistrationTokenResponseDto> CreateAsync(
        CreateRegistrationTokenDto dto, string createdBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var plaintext = IEndpointService.GenerateApiKey();
        var hash      = IEndpointService.HashApiKey(plaintext);
        // null ExpiryHours = default 30 days (previously 100 years)
        var expiresAt = dto.ExpiryHours.HasValue
            ? DateTimeOffset.UtcNow.AddHours(dto.ExpiryHours.Value)
            : DateTimeOffset.UtcNow.AddDays(30);

        var token = new RegistrationToken
        {
            TokenHash = hash,
            Label     = dto.Label.Trim(),
            ExpiresAt = expiresAt,
            MaxUses   = dto.MaxUses,
            CreatedBy = createdBy,
        };

        db.RegistrationTokens.Add(token);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("token.created", createdBy, detail: $"label:{dto.Label}", ct: ct);

        return new CreateRegistrationTokenResponseDto
        {
            TokenId        = token.Id,
            PlaintextToken = plaintext,
            ExpiresAt      = expiresAt,
        };
    }

    private record TokenRow(Guid Id, string Label);

    public async Task<ExchangeTokenResponseDto?> ExchangeAsync(
        ExchangeTokenDto dto, string machineId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hash = IEndpointService.HashApiKey(dto.RegistrationToken);

        // Single atomic UPDATE + RETURNING: eliminates TOCTOU race between SELECT and UPDATE.
        // If the token is missing, expired, inactive, or exhausted, zero rows are returned.
        var updated = await db.Database
            .SqlQueryRaw<TokenRow>(
                """
                UPDATE registration_tokens
                SET "UseCount" = "UseCount" + 1
                WHERE "TokenHash" = {0}
                  AND "IsActive" = true
                  AND "UseCount" < "MaxUses"
                  AND "ExpiresAt" > now()
                RETURNING "Id", "Label"
                """,
                hash)
            .ToListAsync(ct);

        if (updated.Count == 0)
        {
            log.LogWarning("Token exchange failed: token not found, expired, inactive, or exhausted. MachineId={MachineId}", machineId);
            return null;
        }

        var token = updated[0];

        // Generate a new permanent ApiKey for this endpoint
        var newApiKey = IEndpointService.GenerateApiKey();

        var reg = await endpoints.UpsertAsync(machineId, newApiKey, new RegisterEndpointDto
        {
            MachineName  = dto.MachineName,
            OsVersion    = dto.OsVersion,
            AgentVersion = dto.AgentVersion,
        }, ct);

        await audit.LogAsync(
            "endpoint.registered-via-token",
            $"machine:{machineId}",
            targetMachine: dto.MachineName,
            detail: $"label:{token.Label}",
            ct: ct);

        log.LogInformation(
            "Token exchange succeeded. Label={Label} Machine={MachineName} RegistrationId={Id}",
            token.Label, dto.MachineName, reg.Id);

        return new ExchangeTokenResponseDto
        {
            ApiKey         = newApiKey,
            RegistrationId = reg.Id,
        };
    }

    public async Task<List<RegistrationTokenDto>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tokens = await db.RegistrationTokens
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return tokens.Select(t => new RegistrationTokenDto
        {
            Id        = t.Id,
            Label     = t.Label,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt,
            MaxUses   = t.MaxUses,
            UseCount  = t.UseCount,
            IsActive  = t.IsActive,
            CreatedBy = t.CreatedBy,
        }).ToList();
    }

    public async Task RevokeAsync(Guid tokenId, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.RegistrationTokens.FindAsync([tokenId], ct);
        if (token is null) return;

        token.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("token.revoked", actorUpn, detail: $"label:{token.Label}", ct: ct);
    }

    public async Task DeleteAsync(Guid tokenId, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.RegistrationTokens.FindAsync([tokenId], ct);
        if (token is null) return;

        // Only allow deleting inactive tokens (revoked / expired / exhausted)
        if (token.IsActive && DateTimeOffset.UtcNow <= token.ExpiresAt && token.UseCount < token.MaxUses)
            throw new InvalidOperationException("Cannot delete an active token. Revoke it first.");

        db.RegistrationTokens.Remove(token);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("token.deleted", actorUpn, detail: $"label:{token.Label}", ct: ct);
    }
}
