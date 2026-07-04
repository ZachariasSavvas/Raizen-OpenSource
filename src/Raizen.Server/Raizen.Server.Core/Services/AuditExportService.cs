using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IAuditExportService
{
    Task<SignedAuditExportDto> ExportAsync(
        string? eventFilter,
        string? userFilter,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default);
}

public sealed class SignedAuditExportDto
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Algorithm { get; set; } = "HMAC-SHA256";
    public DateTimeOffset ExportedAt { get; set; }
    public int RecordCount { get; set; }
}

public sealed class AuditExportService(IDbContextFactory<RaizenDbContext> dbFactory) : IAuditExportService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Raizen", "audit-signing.key");

    public async Task<SignedAuditExportDto> ExportAsync(
        string? eventFilter,
        string? userFilter,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(eventFilter))
            q = q.Where(x => x.Event.Contains(eventFilter));
        if (!string.IsNullOrEmpty(userFilter))
            q = q.Where(x => x.ActorUpn.Contains(userFilter) ||
                              (x.Request != null && x.Request.RequesterUpn.Contains(userFilter)));
        if (from.HasValue)
            q = q.Where(x => x.OccurredAt >= new DateTimeOffset(from.Value, TimeSpan.Zero));
        if (to.HasValue)
            q = q.Where(x => x.OccurredAt <= new DateTimeOffset(to.Value.AddDays(1), TimeSpan.Zero));

        var items = await q
            .OrderBy(x => x.OccurredAt)
            .Select(x => new AuditLogDto
            {
                Id            = x.Id,
                RequestId     = x.RequestId,
                Event         = x.Event,
                ActorUpn      = x.ActorUpn,
                RequesterUpn  = x.Request != null ? x.Request.RequesterUpn : null,
                ApproverUpn   = x.Request != null ? x.Request.ReviewerUpn  : null,
                TargetMachine = x.Request != null && x.Request.Endpoint != null
                    ? x.Request.Endpoint.MachineName
                    : x.TargetMachine,
                Detail        = x.Detail,
                OccurredAt    = x.OccurredAt,
                IpAddress     = x.IpAddress,
            })
            .ToListAsync(ct);

        var exportedAt = DateTimeOffset.UtcNow;
        var envelope = new
        {
            ExportedAt  = exportedAt,
            RecordCount = items.Count,
            Records     = items,
        };

        var json         = JsonSerializer.Serialize(envelope, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(json);

        var key      = GetOrCreateKey();
        byte[] sig;
        using (var hmac = new HMACSHA256(key))
            sig = hmac.ComputeHash(payloadBytes);

        return new SignedAuditExportDto
        {
            Payload     = Convert.ToBase64String(payloadBytes),
            Signature   = Convert.ToBase64String(sig),
            Algorithm   = "HMAC-SHA256",
            ExportedAt  = exportedAt,
            RecordCount = items.Count,
        };
    }

    private static byte[] GetOrCreateKey()
    {
        if (File.Exists(KeyPath))
            return Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllText(KeyPath, Convert.ToBase64String(key));
        return key;
    }
}
