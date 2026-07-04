using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Shared.DTOs;

namespace Raizen.Server.Api.Controllers;

[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = "AdminOnly")]
public sealed class AuditController(RaizenDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> List(
        [FromQuery] AuditLogQueryDto query,
        CancellationToken ct)
    {
        var q = db.AuditLogs.AsQueryable();

        if (query.From.HasValue) q = q.Where(x => x.OccurredAt >= query.From.Value);
        if (query.To.HasValue) q = q.Where(x => x.OccurredAt <= query.To.Value);
        if (!string.IsNullOrEmpty(query.ActorUpn)) q = q.Where(x => x.ActorUpn == query.ActorUpn);
        if (!string.IsNullOrEmpty(query.TargetMachine)) q = q.Where(x => x.TargetMachine == query.TargetMachine);
        if (!string.IsNullOrEmpty(query.Event)) q = q.Where(x => x.Event == query.Event);
        if (query.RequestId.HasValue) q = q.Where(x => x.RequestId == query.RequestId.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.OccurredAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new AuditLogDto
            {
                Id = x.Id,
                RequestId = x.RequestId,
                Event = x.Event,
                ActorUpn = x.ActorUpn,
                TargetMachine = x.TargetMachine,
                Detail = x.Detail,
                OccurredAt = x.OccurredAt,
                IpAddress = x.IpAddress
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<AuditLogDto>
        {
            Items = items,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize
        });
    }
}
