using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IDashboardService
{
    Task<DashboardStatsDto> GetStatsAsync(CancellationToken ct = default);
}

public sealed class DashboardStatsDto
{
    public int PendingCount    { get; init; }
    public int ApprovedCount   { get; init; }
    public int SucceededToday  { get; init; }
    public int FailedToday     { get; init; }
    public int TotalLast30Days { get; init; }

    /// <summary>Status → count for requests in the last 30 days.</summary>
    public Dictionary<string, int> ByStatus    { get; init; } = [];

    /// <summary>"yyyy-MM-dd" → count for requests submitted in the last 14 days.</summary>
    public Dictionary<string, int> DailyVolume { get; init; } = [];

    /// <summary>ActionType name → count; top 5 action types in the last 30 days.</summary>
    public Dictionary<string, int> ByActionType { get; init; } = [];

    /// <summary>Up to 50 currently-pending requests for the review table.</summary>
    public List<ElevationRequestDto> PendingItems { get; init; } = [];
}
