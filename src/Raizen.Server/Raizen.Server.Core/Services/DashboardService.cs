using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public sealed class DashboardService(IDbContextFactory<RaizenDbContext> dbFactory) : IDashboardService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<DashboardStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now           = DateTimeOffset.UtcNow;
        var thirtyDaysAgo = now.AddDays(-30);
        var fourteenDaysAgo = now.AddDays(-14);
        var todayStart    = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        // Stat card counts
        var pendingCount   = await db.ElevationRequests.CountAsync(x => x.Status == RequestStatus.Pending,   ct);
        var approvedCount  = await db.ElevationRequests.CountAsync(x => x.Status == RequestStatus.Approved,  ct);
        var succeededToday = await db.ElevationRequests.CountAsync(
            x => x.Status == RequestStatus.Succeeded && x.ExecutedAt >= todayStart, ct);
        var failedToday    = await db.ElevationRequests.CountAsync(
            x => x.Status == RequestStatus.Failed    && x.ExecutedAt >= todayStart, ct);

        // Pending review table (up to 50, oldest first)
        var pendingEntities = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .Where(x => x.Status == RequestStatus.Pending)
            .OrderBy(x => x.SubmittedAt)
            .Take(50)
            .ToListAsync(ct);

        // Chart data — last 30 days
        var recent = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Where(x => x.SubmittedAt >= thirtyDaysAgo)
            .Select(x => new
            {
                Status     = x.Status,
                SubmittedAt = x.SubmittedAt,
                ActionType = x.ActionDefinition.ActionType.ToString(),
            })
            .ToListAsync(ct);

        var byStatus = recent
            .GroupBy(x => x.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var byActionType = recent
            .GroupBy(x => x.ActionType)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToDictionary(g => g.Key, g => g.Count());

        // Daily volume — last 14 days (inclusive of today)
        var dailyVolume = new Dictionary<string, int>();
        for (var d = fourteenDaysAgo.UtcDateTime.Date; d <= now.UtcDateTime.Date; d = d.AddDays(1))
        {
            var dayStart = new DateTimeOffset(d, TimeSpan.Zero);
            var dayEnd   = dayStart.AddDays(1);
            dailyVolume[d.ToString("yyyy-MM-dd")] =
                recent.Count(x => x.SubmittedAt >= dayStart && x.SubmittedAt < dayEnd);
        }

        return new DashboardStatsDto
        {
            PendingCount    = pendingCount,
            ApprovedCount   = approvedCount,
            SucceededToday  = succeededToday,
            FailedToday     = failedToday,
            TotalLast30Days = recent.Count,
            ByStatus        = byStatus,
            DailyVolume     = dailyVolume,
            ByActionType    = byActionType,
            PendingItems    = pendingEntities.Select(Map).ToList(),
        };
    }

    private static ElevationRequestDto Map(ElevationRequest r)
    {
        var parameters = string.IsNullOrEmpty(r.ParametersJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParametersJson, JsonOpts) ?? [];

        return new ElevationRequestDto
        {
            Id                     = r.Id,
            EndpointRegistrationId = r.EndpointRegistrationId,
            ActionDefinitionId     = r.ActionDefinitionId,
            ActionDisplayName      = r.ActionDefinition != null ? r.ActionDefinition.DisplayName : "[Deleted Action]",
            ActionType             = r.ActionDefinition != null ? r.ActionDefinition.ActionType : default,
            RequesterUpn           = r.RequesterUpn,
            RequesterDisplayName   = r.RequesterDisplayName,
            TargetMachine          = r.Endpoint != null ? r.Endpoint.MachineName : "[Unknown]",
            MachineId              = r.Endpoint != null ? r.Endpoint.MachineId : "unknown",
            Justification          = r.Justification,
            TicketReference        = r.TicketReference,
            Parameters             = parameters,
            Status                 = r.Status,
            SubmittedAt            = r.SubmittedAt,
            ReviewedAt             = r.ReviewedAt,
            ExecutedAt             = r.ExecutedAt,
            ExpiresAt              = r.ExpiresAt,
            ReviewerUpn            = r.ReviewerUpn,
            ReviewerNote           = r.ReviewerNote,
            ExecutionResult        = r.ExecutionResult,
            ExecutionError         = r.ExecutionError,
        };
    }
}
