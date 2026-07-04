using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public sealed class BulkOperationService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    IActionCatalogService catalog,
    IAuditService audit) : IBulkOperationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<BulkOperationDto> SubmitAsync(
        SubmitBulkOperationDto dto,
        string createdByUpn,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var definition = await catalog.GetEnabledAsync(dto.ActionDefinitionId, ct)
            ?? throw new InvalidOperationException($"Action definition {dto.ActionDefinitionId} not found or disabled.");

        catalog.ValidateParameters(definition, dto.Parameters);

        // Resolve target endpoints
        List<EndpointRegistration> endpoints;
        if (dto.EndpointIds.Count > 0)
        {
            endpoints = await db.EndpointRegistrations
                .Where(e => dto.EndpointIds.Contains(e.Id) && e.IsEnabled)
                .ToListAsync(ct);

            if (endpoints.Count == 0)
                throw new InvalidOperationException("No enabled endpoints found for the specified IDs.");
        }
        else if (!string.IsNullOrWhiteSpace(dto.EndpointTag))
        {
            // Search TagsJson (JSONB array) for matching tag using string containment.
            // TagsJson stores ["tag1","tag2"], so we check for the quoted tag value.
            var tagSearch = $"\"{dto.EndpointTag}\"";
            endpoints = await db.EndpointRegistrations
                .Where(e => e.IsEnabled && e.TagsJson != null
                         && e.TagsJson.Contains(tagSearch))
                .ToListAsync(ct);

            if (endpoints.Count == 0)
                throw new InvalidOperationException($"No enabled endpoints found with tag '{dto.EndpointTag}'.");
        }
        else
        {
            throw new InvalidOperationException("Either EndpointIds or EndpointTag must be specified.");
        }

        var now = DateTimeOffset.UtcNow;
        var parametersJson = JsonSerializer.Serialize(dto.Parameters, JsonOpts);

        var bulk = new BulkOperation
        {
            ActionDefinitionId = definition.Id,
            CreatedByUpn = createdByUpn,
            Justification = dto.Justification,
            TicketReference = dto.TicketReference,
            ParametersJson = parametersJson,
            TotalCount = endpoints.Count,
            CreatedAt = now,
        };
        db.BulkOperations.Add(bulk);

        // Fan out: create one ElevationRequest per endpoint
        foreach (var ep in endpoints)
        {
            var request = new ElevationRequest
            {
                ActionDefinitionId = definition.Id,
                EndpointRegistrationId = ep.Id,
                RequesterUpn = createdByUpn,
                RequesterDisplayName = createdByUpn,
                Justification = dto.Justification,
                TicketReference = dto.TicketReference,
                ParametersJson = parametersJson,
                OriginalParametersJson = parametersJson,
                SubmittedAt = now,
                ExpiresAt = now.AddMinutes(definition.ApprovalWindowMinutes),
                Status = RequestStatus.Pending,
                BulkOperationId = bulk.Id,
            };
            db.ElevationRequests.Add(request);
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("bulk.created", createdByUpn, null,
            null, $"Bulk operation {bulk.Id}: {endpoints.Count} endpoints, action={definition.DisplayName}", ct);

        return MapSummary(bulk, definition);
    }

    public async Task<BulkOperationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bulk = await db.BulkOperations
            .Include(b => b.ActionDefinition)
            .Include(b => b.Requests).ThenInclude(r => r.Endpoint)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (bulk is null) return null;

        var dto = MapSummary(bulk, bulk.ActionDefinition);
        dto.Requests = bulk.Requests.Select(r => new BulkChildRequestDto
        {
            RequestId = r.Id,
            TargetMachine = r.Endpoint.MachineName,
            Status = r.Status,
            ExecutionResult = r.ExecutionResult,
            ExecutionError = r.ExecutionError,
        }).ToList();

        return dto;
    }

    public async Task<PagedResult<BulkOperationDto>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.BulkOperations
            .Include(b => b.ActionDefinition)
            .OrderByDescending(b => b.CreatedAt);

        var total = await q.CountAsync(ct);
        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<BulkOperationDto>
        {
            Items = items.Select(b => MapSummary(b, b.ActionDefinition)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    private static BulkOperationDto MapSummary(BulkOperation b, ActionDefinition def) => new()
    {
        Id = b.Id,
        ActionDefinitionId = b.ActionDefinitionId,
        ActionDisplayName = def.DisplayName,
        ActionType = def.ActionType,
        CreatedByUpn = b.CreatedByUpn,
        Justification = b.Justification,
        TicketReference = b.TicketReference,
        Parameters = string.IsNullOrEmpty(b.ParametersJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(b.ParametersJson, JsonOpts) ?? [],
        TotalCount = b.TotalCount,
        SucceededCount = b.SucceededCount,
        FailedCount = b.FailedCount,
        CreatedAt = b.CreatedAt,
        CompletedAt = b.CompletedAt,
    };
}
