using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public interface IActionCatalogService
{
    Task<ActionDefinition?> GetEnabledAsync(Guid id, CancellationToken ct = default);
    Task<List<ActionDefinitionDto>> ListAsync(bool includeDisabled = false, CancellationToken ct = default);
    Task<ActionDefinitionDto> CreateAsync(UpsertActionDefinitionDto dto, string createdByUpn, CancellationToken ct = default);
    Task<ActionDefinitionDto> UpdateAsync(Guid id, UpsertActionDefinitionDto dto, string updatedByUpn, CancellationToken ct = default);
    Task SetEnabledAsync(Guid id, bool enabled, string actorUpn, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a disabled action definition.
    /// Throws if the action is enabled or has linked elevation requests.
    /// </summary>
    Task DeleteAsync(Guid id, string actorUpn, CancellationToken ct = default);

    /// <summary>Validates parameter values against the definition's schema. Throws on failure.</summary>
    void ValidateParameters(ActionDefinition definition, Dictionary<string, string> parameters);
}

public sealed class ActionCatalogService(IDbContextFactory<RaizenDbContext> dbFactory, IAuditService audit) : IActionCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ActionType values that have a corresponding IActionHandler on the endpoint agent.
    /// Creating an action definition for an unimplemented type will be rejected.
    /// </summary>
    private static readonly HashSet<ActionType> ImplementedTypes =
    [
        ActionType.InstallMsi,
        ActionType.AddLocalGroupMember,
        ActionType.RemoveLocalGroupMember,
        ActionType.StartService,
        ActionType.StopService,
        ActionType.RestartService,
        ActionType.CopyFile,
        ActionType.SetRegistryValue,
        ActionType.RunApprovedScript,
        ActionType.OpenFileProperties,
        ActionType.RunAsAdmin,
        ActionType.SetNetworkConfiguration,
        ActionType.SetEnvironmentVariable,
    ];

    public async Task<ActionDefinition?> GetEnabledAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ActionDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.IsEnabled, ct);
    }

    public async Task<List<ActionDefinitionDto>> ListAsync(bool includeDisabled = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.ActionDefinitions.AsQueryable();
        if (!includeDisabled) q = q.Where(x => x.IsEnabled);
        var items = await q.OrderBy(x => x.DisplayName).ToListAsync(ct);
        var dtos = items.Select(MapDto).ToList();

        // Enrich with average approval time (exclude auto-approved, require >= 3 samples)
        var actionIds = items.Select(x => x.Id).ToList();
        var stats = await db.ElevationRequests
            .Where(r => actionIds.Contains(r.ActionDefinitionId)
                && r.ReviewedAt.HasValue
                && r.ReviewerUpn != "system:auto-approve")
            .GroupBy(r => r.ActionDefinitionId)
            .Select(g => new
            {
                ActionDefinitionId = g.Key,
                Count = g.Count(),
                TotalSeconds = g.Sum(r => (double)(r.ReviewedAt!.Value - r.SubmittedAt).TotalSeconds),
            })
            .ToListAsync(ct);

        var statsLookup = stats
            .Where(x => x.Count >= 3)
            .ToDictionary(x => x.ActionDefinitionId, x => (int)(x.TotalSeconds / x.Count));
        foreach (var dto in dtos)
            if (statsLookup.TryGetValue(dto.Id, out var avgSec))
                dto.AverageApprovalSeconds = avgSec;

        return dtos;
    }

    public async Task<ActionDefinitionDto> CreateAsync(
        UpsertActionDefinitionDto dto, string createdByUpn, CancellationToken ct = default)
    {
        if (!ImplementedTypes.Contains(dto.ActionType))
            throw new InvalidOperationException(
                $"ActionType '{dto.ActionType}' does not have an endpoint handler yet. " +
                $"Supported types: {string.Join(", ", ImplementedTypes.Order())}.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = new ActionDefinition
        {
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            ActionType = dto.ActionType,
            ParametersSchemaJson = JsonSerializer.Serialize(dto.Parameters, JsonOpts),
            ApproverGroupIdsJson = JsonSerializer.Serialize(dto.ApproverGroupIds, JsonOpts),
            AutoApprove = dto.AutoApprove,
            AutoApproveConditionDescription = dto.AutoApproveConditionDescription,
            ApprovalWindowMinutes = dto.ApprovalWindowMinutes,
            MinApprovers = Math.Max(1, dto.MinApprovers),
            IsEnabled = dto.IsEnabled,
            CreatedByUpn = createdByUpn,
        };
        db.ActionDefinitions.Add(entity);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("action.created", createdByUpn, detail: dto.DisplayName, ct: ct);
        return MapDto(entity);
    }

    public async Task<ActionDefinitionDto> UpdateAsync(
        Guid id, UpsertActionDefinitionDto dto, string updatedByUpn, CancellationToken ct = default)
    {
        if (!ImplementedTypes.Contains(dto.ActionType))
            throw new InvalidOperationException(
                $"ActionType '{dto.ActionType}' does not have an endpoint handler yet. " +
                $"Supported types: {string.Join(", ", ImplementedTypes.Order())}.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ActionDefinitions.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Action definition {id} not found.");

        entity.DisplayName = dto.DisplayName;
        entity.Description = dto.Description;
        entity.ActionType = dto.ActionType;
        entity.ParametersSchemaJson = JsonSerializer.Serialize(dto.Parameters, JsonOpts);
        entity.ApproverGroupIdsJson = JsonSerializer.Serialize(dto.ApproverGroupIds, JsonOpts);
        entity.AutoApprove = dto.AutoApprove;
        entity.AutoApproveConditionDescription = dto.AutoApproveConditionDescription;
        entity.ApprovalWindowMinutes = dto.ApprovalWindowMinutes;
        entity.MinApprovers = Math.Max(1, dto.MinApprovers);
        entity.IsEnabled = dto.IsEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("action.updated", updatedByUpn, detail: dto.DisplayName, ct: ct);
        return MapDto(entity);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ActionDefinitions.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Action definition {id} not found.");
        entity.IsEnabled = enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var evt = enabled ? "action.enabled" : "action.disabled";
        await audit.LogAsync(evt, actorUpn, detail: entity.DisplayName, ct: ct);
    }

    public async Task DeleteAsync(Guid id, string actorUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ActionDefinitions.FindAsync([id], ct)
            ?? throw new InvalidOperationException("Action definition not found.");

        if (entity.IsEnabled)
            throw new InvalidOperationException("Disable the action before deleting it.");

        var hasRequests = await db.ElevationRequests
            .AnyAsync(r => r.ActionDefinitionId == id, ct);
        if (hasRequests)
            throw new InvalidOperationException(
                "Cannot delete: this action has linked elevation requests. Archive them first.");

        db.ActionDefinitions.Remove(entity);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("action.deleted", actorUpn, detail: entity.DisplayName, ct: ct);
    }

    public void ValidateParameters(ActionDefinition definition, Dictionary<string, string> parameters)
    {
        var schema = JsonSerializer.Deserialize<List<ActionParameterDefinitionDto>>(
            definition.ParametersSchemaJson, JsonOpts) ?? [];

        var errors = new List<string>();

        foreach (var paramDef in schema)
        {
            if (!parameters.TryGetValue(paramDef.Key, out var value))
            {
                if (paramDef.Required)
                    errors.Add($"Required parameter '{paramDef.Key}' is missing.");
                continue;
            }

            if (!string.IsNullOrEmpty(paramDef.ValidationPattern))
            {
                if (!Regex.IsMatch(value, paramDef.ValidationPattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    errors.Add($"Parameter '{paramDef.Key}' value '{value}' does not match expected pattern.");
            }
        }

        // Reject unexpected parameters (no extras allowed — reduces attack surface)
        var allowedKeys = schema.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in parameters.Keys)
        {
            if (!allowedKeys.Contains(key))
                errors.Add($"Unexpected parameter '{key}'.");
        }

        if (errors.Count > 0)
            throw new ArgumentException("Parameter validation failed: " + string.Join("; ", errors));
    }

    private static ActionDefinitionDto MapDto(ActionDefinition e)
    {
        var paramOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return new ActionDefinitionDto
        {
            Id = e.Id,
            DisplayName = e.DisplayName,
            Description = e.Description,
            ActionType = e.ActionType,
            Parameters = JsonSerializer.Deserialize<List<ActionParameterDefinitionDto>>(
                e.ParametersSchemaJson, paramOpts) ?? [],
            ApproverGroupIds = JsonSerializer.Deserialize<List<string>>(
                e.ApproverGroupIdsJson, paramOpts) ?? [],
            AutoApprove = e.AutoApprove,
            AutoApproveConditionDescription = e.AutoApproveConditionDescription,
            ApprovalWindowMinutes = e.ApprovalWindowMinutes,
            MinApprovers = e.MinApprovers,
            IsEnabled = e.IsEnabled,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }
}
