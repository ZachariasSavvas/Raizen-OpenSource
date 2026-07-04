using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public interface IAutoApprovalService
{
    Task<bool> ShouldAutoApproveAsync(ActionType actionType, Guid actionDefinitionId, string requesterUpn, CancellationToken ct = default);
    Task<List<AutoApprovalRuleDto>> ListRulesAsync(CancellationToken ct = default);
    Task<AutoApprovalRuleDto> CreateRuleAsync(CreateAutoApprovalRuleDto dto, string createdBy, CancellationToken ct = default);
    Task<bool> ToggleRuleAsync(Guid ruleId, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid ruleId, CancellationToken ct = default);
}

public sealed class AutoApprovalService(IDbContextFactory<RaizenDbContext> dbFactory) : IAutoApprovalService
{
    public async Task<bool> ShouldAutoApproveAsync(
        ActionType actionType,
        Guid actionDefinitionId,
        string requesterUpn,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rules = await db.AutoApprovalRules
            .Where(r => r.IsEnabled)
            .ToListAsync(ct);

        foreach (var rule in rules)
        {
            // Check action type filter
            if (rule.ActionType.HasValue && rule.ActionType.Value != actionType)
                continue;

            // Check action definition filter
            if (rule.ActionDefinitionId.HasValue && rule.ActionDefinitionId.Value != actionDefinitionId)
                continue;

            // Check UPN pattern
            if (!string.IsNullOrEmpty(rule.RequesterUpnPattern) &&
                !MatchesPattern(requesterUpn, rule.RequesterUpnPattern))
                continue;

            return true;
        }

        return false;
    }

    public async Task<List<AutoApprovalRuleDto>> ListRulesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AutoApprovalRules
            .OrderBy(r => r.Name)
            .Select(r => Map(r))
            .ToListAsync(ct);
    }

    public async Task<AutoApprovalRuleDto> CreateRuleAsync(
        CreateAutoApprovalRuleDto dto,
        string createdBy,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = new AutoApprovalRule
        {
            Name                = dto.Name,
            ActionType          = dto.ActionType,
            ActionDefinitionId  = dto.ActionDefinitionId,
            RequesterUpnPattern = dto.RequesterUpnPattern,
            IsEnabled           = dto.IsEnabled,
            CreatedBy           = createdBy,
        };
        db.AutoApprovalRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Map(rule);
    }

    public async Task<bool> ToggleRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.AutoApprovalRules.FindAsync([ruleId], ct)
            ?? throw new InvalidOperationException("Rule not found.");
        rule.IsEnabled = !rule.IsEnabled;
        await db.SaveChangesAsync(ct);
        return rule.IsEnabled;
    }

    public async Task DeleteRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.AutoApprovalRules.FindAsync([ruleId], ct)
            ?? throw new InvalidOperationException("Rule not found.");
        db.AutoApprovalRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        // Simple glob: * matches any sequence of characters
        if (pattern == "*") return true;
        if (!pattern.Contains('*'))
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);

        var parts = pattern.Split('*');
        var remaining = value.AsSpan();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].AsSpan();
            if (i == 0)
            {
                if (!remaining.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
                remaining = remaining[part.Length..];
            }
            else if (i == parts.Length - 1)
            {
                if (!remaining.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                var idx = remaining.ToString().IndexOf(parts[i], StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                remaining = remaining[(idx + part.Length)..];
            }
        }
        return true;
    }

    private static AutoApprovalRuleDto Map(AutoApprovalRule r) => new()
    {
        Id                  = r.Id,
        Name                = r.Name,
        ActionType          = r.ActionType,
        ActionDefinitionId  = r.ActionDefinitionId,
        RequesterUpnPattern = r.RequesterUpnPattern,
        IsEnabled           = r.IsEnabled,
        CreatedBy           = r.CreatedBy,
        CreatedAt           = r.CreatedAt,
    };
}
