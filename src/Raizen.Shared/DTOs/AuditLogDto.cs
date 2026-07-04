namespace Raizen.Shared.DTOs;

public sealed class AuditLogDto
{
    public Guid Id { get; set; }
    public Guid? RequestId { get; set; }
    public string Event { get; set; } = string.Empty;
    public string ActorUpn { get; set; } = string.Empty;
    /// <summary>UPN of the end-user who submitted the elevation request (null for non-request events).</summary>
    public string? RequesterUpn { get; set; }
    /// <summary>UPN of the admin who approved or denied the request (null when not yet reviewed).</summary>
    public string? ApproverUpn { get; set; }
    public string? TargetMachine { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? IpAddress { get; set; }
}

public sealed class AuditLogQueryDto
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string? ActorUpn { get; set; }
    public string? TargetMachine { get; set; }
    public string? Event { get; set; }
    public Guid? RequestId { get; set; }

    private int _page = 1;
    public int Page { get => _page; set => _page = Math.Max(1, value); }

    private int _pageSize = 50;
    public int PageSize { get => _pageSize; set => _pageSize = Math.Clamp(value, 1, 500); }
}

public sealed class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}
