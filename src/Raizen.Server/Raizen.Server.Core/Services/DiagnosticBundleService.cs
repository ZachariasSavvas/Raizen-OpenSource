using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public interface IDiagnosticBundleService
{
    Task<DiagnosticBundleUploadResponseDto> UploadAsync(Guid registrationId, DiagnosticBundleUploadDto dto, CancellationToken ct = default);
    Task<List<DiagnosticBundleDto>> ListAsync(Guid endpointId, CancellationToken ct = default);
    Task<(byte[] Content, string ContentType, string FileName)?> DownloadAsync(Guid id, CancellationToken ct = default);
    Task DeleteExpiredAsync(CancellationToken ct = default);
}

public sealed class DiagnosticBundleService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    IAuditService audit) : IDiagnosticBundleService
{
    internal const int MaxBundleBytes = 10 * 1024 * 1024;

    public async Task<DiagnosticBundleUploadResponseDto> UploadAsync(
        Guid registrationId,
        DiagnosticBundleUploadDto dto,
        CancellationToken ct = default)
    {
        if (dto.RequestId == Guid.Empty)
            throw new ArgumentException("RequestId is required.");
        if (dto.EventCount < 0 || dto.EventCount > 1000)
            throw new ArgumentException("EventCount must be between 0 and 1000.");
        if (!IsSafeFileName(dto.FileName))
            throw new ArgumentException("FileName must be a simple .zip file name.");
        if (string.IsNullOrWhiteSpace(dto.Sha256)
            || dto.Sha256.Length != 64
            || dto.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Sha256 must be a 64-character hexadecimal hash.");

        byte[] content;
        try { content = Convert.FromBase64String(dto.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("ContentBase64 is invalid."); }
        if (content.Length == 0 || content.Length > MaxBundleBytes)
            throw new ArgumentException($"Diagnostic bundle must be between 1 and {MaxBundleBytes} bytes.");

        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualHash),
                System.Text.Encoding.ASCII.GetBytes(dto.Sha256.ToLowerInvariant())))
            throw new ArgumentException("Diagnostic bundle SHA-256 does not match its content.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .SingleOrDefaultAsync(x => x.Id == dto.RequestId, ct)
            ?? throw new InvalidOperationException("Diagnostic request was not found.");

        if (request.EndpointRegistrationId != registrationId
            || request.ActionDefinition.ActionType != ActionType.CollectEventLogs
            || request.Status != RequestStatus.Executing)
            throw new InvalidOperationException("Diagnostic upload does not match an executing request for this endpoint.");

        if (await db.DiagnosticBundles.AnyAsync(x => x.RequestId == dto.RequestId, ct))
            throw new InvalidOperationException("A diagnostic bundle already exists for this request.");

        var bundle = new DiagnosticBundle
        {
            EndpointRegistrationId = registrationId,
            RequestId = dto.RequestId,
            FileName = dto.FileName,
            Content = content,
            Sha256 = actualHash,
            SizeBytes = content.Length,
            EventCount = dto.EventCount,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.DiagnosticBundles.Add(bundle);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            "diagnostics.uploaded",
            $"endpoint:{request.Endpoint.MachineName}",
            dto.RequestId,
            request.Endpoint.MachineName,
            $"bundle={bundle.Id}; events={bundle.EventCount}; bytes={bundle.SizeBytes}; sha256={bundle.Sha256}",
            ct);

        return new DiagnosticBundleUploadResponseDto { Id = bundle.Id, ExpiresAt = bundle.ExpiresAt };
    }

    public async Task<List<DiagnosticBundleDto>> ListAsync(Guid endpointId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DiagnosticBundles.AsNoTracking()
            .Where(x => x.EndpointRegistrationId == endpointId && x.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new DiagnosticBundleDto
            {
                Id = x.Id,
                EndpointRegistrationId = x.EndpointRegistrationId,
                RequestId = x.RequestId,
                MachineName = x.Endpoint.MachineName,
                FileName = x.FileName,
                Sha256 = x.Sha256,
                SizeBytes = x.SizeBytes,
                EventCount = x.EventCount,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt,
            })
            .ToListAsync(ct);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)?> DownloadAsync(
        Guid id,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bundle = await db.DiagnosticBundles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ExpiresAt > DateTimeOffset.UtcNow, ct);
        return bundle is null ? null : (bundle.Content, bundle.ContentType, bundle.FileName);
    }

    public async Task DeleteExpiredAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.DiagnosticBundles
            .Where(x => x.ExpiresAt <= DateTimeOffset.UtcNow)
            .ExecuteDeleteAsync(ct);
    }

    internal static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Length <= 120
        && fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(fileName) == fileName
        && fileName.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
}
