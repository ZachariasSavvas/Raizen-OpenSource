using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Server.Core.Services;

public sealed class RequestService(
    IDbContextFactory<RaizenDbContext> dbFactory,
    IAuditService audit,
    IActionCatalogService catalog,
    INotificationService notifications,
    IAutoApprovalService autoApproval,
    IConfiguration config) : IRequestService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int MaxCommentLength = 4000;

    public async Task<ElevationRequestDto> SubmitAsync(
        SubmitElevationRequestDto dto,
        string requesterUpn,
        string requesterDisplayName,
        Guid endpointRegistrationId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var definition = await catalog.GetEnabledAsync(dto.ActionDefinitionId, ct)
            ?? throw new InvalidOperationException($"Action definition {dto.ActionDefinitionId} not found or disabled.");

        catalog.ValidateParameters(definition, dto.Parameters);

        var endpoint = await db.EndpointRegistrations
            .FirstOrDefaultAsync(e => e.Id == endpointRegistrationId && e.IsEnabled, ct)
            ?? throw new InvalidOperationException("Endpoint not found or disabled.");

        // ── Rate limiting: max requests per user per hour ────────────────────
        var maxPerHour = int.TryParse(config["RequestLimits:MaxPerUserPerHour"], out var cfgMax) ? cfgMax : 20;
        var hourAgo    = DateTimeOffset.UtcNow.AddHours(-1);
        var recentCount = await db.ElevationRequests
            .CountAsync(r => r.RequesterUpn == requesterUpn && r.SubmittedAt >= hourAgo, ct);
        if (recentCount >= maxPerHour)
            throw new InvalidOperationException(
                $"Rate limit exceeded: you may submit at most {maxPerHour} requests per hour.");

        var now = DateTimeOffset.UtcNow;

        // ── Auto-approval: definition flag OR matching rule ──────────────────
        var ruleAutoApprove = !definition.AutoApprove &&
            await autoApproval.ShouldAutoApproveAsync(definition.ActionType, definition.Id, requesterUpn, ct);
        var shouldAutoApprove = definition.AutoApprove || ruleAutoApprove;

        // Validate scheduled time if provided
        if (dto.ScheduledForUtc.HasValue && dto.ScheduledForUtc.Value <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("ScheduledForUtc must be in the future.");

        var request = new ElevationRequest
        {
            ActionDefinitionId = definition.Id,
            EndpointRegistrationId = endpointRegistrationId,
            RequesterUpn = requesterUpn,
            RequesterDisplayName = requesterDisplayName,
            Justification = dto.Justification,
            TicketReference = dto.TicketReference,
            ParametersJson = JsonSerializer.Serialize(dto.Parameters, JsonOpts),
            OriginalParametersJson = JsonSerializer.Serialize(dto.Parameters, JsonOpts),
            SubmittedAt = now,
            ExpiresAt = now.AddMinutes(definition.ApprovalWindowMinutes),
            Status = shouldAutoApprove ? RequestStatus.Approved : RequestStatus.Pending,
            ScheduledForUtc = dto.ScheduledForUtc,
        };

        if (shouldAutoApprove)
        {
            request.ReviewedAt = now;
            request.ReviewerUpn = "system:auto-approve";
            request.ReviewerNote = ruleAutoApprove
                ? "Auto-approved by matching auto-approval rule."
                : (definition.AutoApproveConditionDescription ?? "Auto-approved by policy.");
        }

        db.ElevationRequests.Add(request);
        await db.SaveChangesAsync(ct);

        var eventName = shouldAutoApprove ? "request.auto-approved" : "request.submitted";
        await audit.LogAsync(eventName, requesterUpn, request.Id, endpoint.MachineName, null, ct);

        var result = Map(request, definition, endpoint);

        if (!shouldAutoApprove)
        {
            // Notify connected admin portals via PostgreSQL LISTEN/NOTIFY
            try { await db.Database.ExecuteSqlRawAsync("SELECT pg_notify('raizen_new_request', {0})", [request.Id.ToString()], ct); } catch { }
            try { await notifications.SendRequestSubmittedAsync(result, ct); } catch { /* notification must not block */ }
        }

        return result;
    }

    public async Task<ElevationRequestDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var r = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .Include(x => x.Approvals)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : Map(r, r.ActionDefinition, r.Endpoint);
    }

    public async Task<PagedResult<ElevationRequestDto>> ListAsync(
        RequestStatus? status,
        string? requesterUpn,
        string? machineId,
        int page,
        int pageSize,
        CancellationToken ct = default,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .Include(x => x.Approvals)
            .AsQueryable();

        if (status.HasValue)
            q = q.Where(x => x.Status == status.Value);
        if (!string.IsNullOrEmpty(requesterUpn))
            q = q.Where(x => x.RequesterUpn == requesterUpn);
        if (!string.IsNullOrEmpty(machineId))
            q = q.Where(x => x.Endpoint.MachineId == machineId);
        if (from.HasValue)
            q = q.Where(x => x.SubmittedAt >= from.Value);
        if (to.HasValue)
            q = q.Where(x => x.SubmittedAt <= to.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ElevationRequestDto>
        {
            Items = items.Select(r => Map(r, r.ActionDefinition, r.Endpoint)).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ElevationRequests.CountAsync(r => r.Status == RequestStatus.Pending, ct);
    }

    public async Task<List<ElevationRequestDto>> GetPendingExecutionAsync(
        Guid endpointRegistrationId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var items = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .Where(x => x.EndpointRegistrationId == endpointRegistrationId
                     && x.Status == RequestStatus.Approved
                     && x.ExpiresAt > now
                     && (x.ScheduledForUtc == null || x.ScheduledForUtc <= now))
            .OrderBy(x => x.ReviewedAt)
            .ToListAsync(ct);

        return items.Select(r => Map(r, r.ActionDefinition, r.Endpoint)).ToList();
    }

    public async Task<ElevationRequestDto> ReviewAsync(
        Guid requestId,
        ReviewRequestDto review,
        string reviewerUpn,
        CancellationToken ct = default)
    {
        reviewerUpn = NormalizeApproverUpn(reviewerUpn);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .Include(x => x.Approvals)
            .FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw new InvalidOperationException("Request not found.");

        if (request.Status != RequestStatus.Pending)
            throw new InvalidOperationException($"Cannot review a request in status {request.Status}.");

        if (request.ExpiresAt < DateTimeOffset.UtcNow)
        {
            request.Status = RequestStatus.Expired;
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Request has expired.");
        }

        var minApprovers = request.ActionDefinition.MinApprovers;

        // Denial is always immediate regardless of MinApprovers
        if (!review.Approved)
        {
            db.RequestApprovals.Add(new RequestApproval
            {
                RequestId   = requestId,
                ApproverUpn = reviewerUpn,
                Note        = review.Note,
                Approved    = false,
            });
            request.Status     = RequestStatus.Denied;
            request.ReviewedAt = DateTimeOffset.UtcNow;
            request.ReviewerUpn  = reviewerUpn;
            request.ReviewerNote = review.Note;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("request.denied", reviewerUpn, requestId, request.Endpoint.MachineName, review.Note, ct);
            var denied = Map(request, request.ActionDefinition, request.Endpoint);
            try { await notifications.SendRequestReviewedAsync(denied, ct); } catch { }
            return denied;
        }

        // Approval path
        if (request.Approvals.Any(a => a.Approved
            && string.Equals(a.ApproverUpn, reviewerUpn, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("You have already approved this request.");

        db.RequestApprovals.Add(new RequestApproval
        {
            RequestId   = requestId,
            ApproverUpn = reviewerUpn,
            Note        = review.Note,
            Approved    = true,
        });

        // Persist the vote first so the DB count is accurate under concurrent access
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            var duplicate = await db.RequestApprovals
                .AsNoTracking()
                .AnyAsync(a => a.RequestId == requestId
                    && a.Approved
                    && a.ApproverUpn.ToLower() == reviewerUpn, ct);
            if (duplicate)
                throw new InvalidOperationException("You have already approved this request.");
            throw;
        }

        // Query the DB directly instead of relying on in-memory navigation fixup,
        // which can be stale if two approvers submit simultaneously.
        var approvedCount = await db.RequestApprovals
            .CountAsync(a => a.RequestId == requestId && a.Approved, ct);

        if (approvedCount >= minApprovers)
        {
            // All required approvals collected — fully approve
            request.Status     = RequestStatus.Approved;
            request.ReviewedAt = DateTimeOffset.UtcNow;
            request.ReviewerUpn  = reviewerUpn;
            request.ReviewerNote = review.Note;

            string? overrideDetail = null;
            if (review.OverrideParameters is { Count: > 0 })
            {
                // Override keys must be a subset of the original parameter keys to prevent
                // approvers from injecting parameters the requester never submitted.
                var originalParams = JsonSerializer
                    .Deserialize<Dictionary<string, string>>(request.ParametersJson ?? "{}", JsonOpts) ?? new();
                var originalKeys = originalParams.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var illegalKeys = review.OverrideParameters.Keys
                    .Where(k => !originalKeys.Contains(k))
                    .ToList();
                if (illegalKeys.Count > 0)
                    throw new InvalidOperationException(
                        $"Override parameters contain keys not present in the original request: {string.Join(", ", illegalKeys)}.");

                // Merge overrides into original params so unmodified params are preserved.
                // Use the original key's casing to prevent case-different duplicates.
                foreach (var kvp in review.OverrideParameters)
                {
                    var originalKey = originalParams.Keys
                        .FirstOrDefault(k => string.Equals(k, kvp.Key, StringComparison.OrdinalIgnoreCase))
                        ?? kvp.Key;
                    originalParams[originalKey] = kvp.Value;
                }

                catalog.ValidateParameters(request.ActionDefinition, originalParams);
                request.ParametersJson = JsonSerializer.Serialize(originalParams, JsonOpts);
                overrideDetail = $"{review.Note}; overrides={JsonSerializer.Serialize(review.OverrideParameters, JsonOpts)}".TrimStart(';', ' ');
            }

            await db.SaveChangesAsync(ct);
            await audit.LogAsync("request.approved", reviewerUpn, requestId, request.Endpoint.MachineName, overrideDetail ?? review.Note, ct);
            var approved = Map(request, request.ActionDefinition, request.Endpoint);
            try { await notifications.SendRequestReviewedAsync(approved, ct); } catch { }
            return approved;
        }
        else
        {
            // Still needs more approvals — remain Pending (vote already persisted above)
            await audit.LogAsync("request.partially-approved", reviewerUpn, requestId,
                request.Endpoint.MachineName, $"{approvedCount}/{minApprovers} approvals", ct);
            return Map(request, request.ActionDefinition, request.Endpoint);
        }
    }

    public async Task<ElevationRequestDto> CancelAsync(
        Guid requestId,
        string requesterUpn,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw new InvalidOperationException("Request not found.");

        if (request.RequesterUpn != requesterUpn)
            throw new UnauthorizedAccessException("Only the requester can cancel.");

        if (request.Status is not (RequestStatus.Pending))
            throw new InvalidOperationException($"Cannot cancel a request in status {request.Status}.");

        request.Status = RequestStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("request.cancelled", requesterUpn, requestId, request.Endpoint.MachineName, null, ct);

        return Map(request, request.ActionDefinition, request.Endpoint);
    }

    public async Task<ElevationRequestDto> ReportExecutionResultAsync(
        ExecutionResultDto result,
        Guid callerRegistrationId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .FirstOrDefaultAsync(x => x.Id == result.RequestId
                                   && x.EndpointRegistrationId == callerRegistrationId, ct)
            ?? throw new InvalidOperationException("Request not found.");

        if (request.Status != RequestStatus.Executing)
            throw new InvalidOperationException($"Unexpected status {request.Status} when reporting result.");

        request.Status = result.Succeeded ? RequestStatus.Succeeded : RequestStatus.Failed;
        request.ExecutedAt = result.ExecutedAt;
        request.ExecutionResult = result.ResultMessage;
        request.ExecutionError = result.ErrorMessage;
        await db.SaveChangesAsync(ct);

        // Update bulk operation counters if this request is part of a bulk op
        if (request.BulkOperationId.HasValue)
        {
            var bulk = await db.Set<Models.BulkOperation>()
                .FirstOrDefaultAsync(b => b.Id == request.BulkOperationId.Value, ct);
            if (bulk is not null)
            {
                if (result.Succeeded) bulk.SucceededCount++;
                else bulk.FailedCount++;

                if (bulk.SucceededCount + bulk.FailedCount >= bulk.TotalCount)
                    bulk.CompletedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
            }
        }

        var evt = result.Succeeded ? "request.succeeded" : "request.failed";
        var detail = result.Succeeded ? result.ResultMessage : result.ErrorMessage;
        await audit.LogAsync(evt, $"endpoint:{request.Endpoint.MachineName}", result.RequestId,
            request.Endpoint.MachineName, detail, ct);

        var mapped = Map(request, request.ActionDefinition, request.Endpoint);
        try { await notifications.SendRequestCompletedAsync(mapped, ct); } catch { }
        return mapped;
    }

    public async Task<ElevationRequestDto?> MarkAsExecutingAsync(Guid requestId, Guid callerRegistrationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.ElevationRequests
            .Include(x => x.ActionDefinition)
            .Include(x => x.Endpoint)
            .FirstOrDefaultAsync(x => x.Id == requestId
                                   && x.EndpointRegistrationId == callerRegistrationId, ct);

        if (request is null || request.Status != RequestStatus.Approved)
            return null;

        if (request.ExpiresAt < DateTimeOffset.UtcNow)
        {
            request.Status = RequestStatus.Expired;
            await db.SaveChangesAsync(ct);
            return null;
        }

        request.Status = RequestStatus.Executing;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another endpoint won the race — do not execute
            return null;
        }

        await audit.LogAsync("request.executing", $"endpoint:{request.Endpoint.MachineName}",
            requestId, request.Endpoint.MachineName, null, ct);

        return Map(request, request.ActionDefinition, request.Endpoint);
    }

    public async Task ExpireStaleRequestsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var stale = await db.ElevationRequests
            .Where(x => (x.Status == RequestStatus.Pending || x.Status == RequestStatus.Approved)
                     && x.ExpiresAt < now)
            .ToListAsync(ct);

        foreach (var r in stale)
            r.Status = RequestStatus.Expired;

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    // ── Approvals ────────────────────────────────────────────────────────────

    public async Task<List<RequestApprovalDto>> GetApprovalsAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RequestApprovals
            .Where(a => a.RequestId == requestId)
            .OrderBy(a => a.OccurredAt)
            .Select(a => new RequestApprovalDto
            {
                Id          = a.Id,
                ApproverUpn = a.ApproverUpn,
                Note        = a.Note,
                Approved    = a.Approved,
                OccurredAt  = a.OccurredAt,
            })
            .ToListAsync(ct);
    }

    // ── Comments ─────────────────────────────────────────────────────────────

    public async Task<List<RequestCommentDto>> GetCommentsAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RequestComments
            .Where(c => c.RequestId == requestId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => MapComment(c))
            .ToListAsync(ct);
    }

    public async Task<RequestCommentDto> AddCommentAsync(
        Guid requestId,
        AddCommentDto dto,
        string authorUpn,
        string authorDisplayName,
        bool isAdmin,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var body = dto.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Comment body cannot be empty.");
        if (body.Length > MaxCommentLength)
            throw new ArgumentException($"Comment is too long. Keep it under {MaxCommentLength} characters.");

        var exists = await db.ElevationRequests.AnyAsync(r => r.Id == requestId, ct);
        if (!exists)
            throw new InvalidOperationException("Request not found.");

        var comment = new RequestComment
        {
            RequestId          = requestId,
            AuthorUpn          = authorUpn,
            AuthorDisplayName  = authorDisplayName,
            IsAdmin            = isAdmin,
            Body               = body,
        };
        db.RequestComments.Add(comment);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("request.comment.added", authorUpn, requestId, null, null, ct);
        return MapComment(comment);
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static ElevationRequestDto Map(
        ElevationRequest r,
        ActionDefinition def,
        EndpointRegistration ep)
    {
        var parameters = string.IsNullOrEmpty(r.ParametersJson)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParametersJson, JsonOpts) ?? [];

        return new ElevationRequestDto
        {
            Id                     = r.Id,
            EndpointRegistrationId = r.EndpointRegistrationId,
            ActionDefinitionId     = r.ActionDefinitionId,
            ActionDisplayName      = def.DisplayName,
            ActionType             = def.ActionType,
            RequesterUpn           = r.RequesterUpn,
            RequesterDisplayName   = r.RequesterDisplayName,
            TargetMachine          = ep.MachineName,
            MachineId              = ep.MachineId,
            Justification          = r.Justification,
            TicketReference        = r.TicketReference,
            Parameters             = parameters,
            Status                 = r.Status,
            SubmittedAt            = r.SubmittedAt,
            ReviewedAt             = r.ReviewedAt,
            ExecutedAt             = r.ExecutedAt,
            ExpiresAt              = r.ExpiresAt,
            ScheduledForUtc        = r.ScheduledForUtc,
            BulkOperationId        = r.BulkOperationId,
            ReviewerUpn            = r.ReviewerUpn,
            ReviewerNote           = r.ReviewerNote,
            ExecutionResult        = r.ExecutionResult,
            ExecutionError         = r.ExecutionError,
            MinApprovers           = def.MinApprovers,
            Approvals              = r.Approvals.Select(a => new RequestApprovalDto
            {
                Id          = a.Id,
                ApproverUpn = a.ApproverUpn,
                Note        = a.Note,
                Approved    = a.Approved,
                OccurredAt  = a.OccurredAt,
            }).ToList(),
        };
    }

    private static RequestCommentDto MapComment(RequestComment c) => new()
    {
        Id                  = c.Id,
        RequestId           = c.RequestId,
        AuthorUpn           = c.AuthorUpn,
        AuthorDisplayName   = c.AuthorDisplayName,
        IsAdmin             = c.IsAdmin,
        Body                = c.Body,
        CreatedAt           = c.CreatedAt,
    };

    private static string NormalizeApproverUpn(string reviewerUpn)
    {
        var normalized = reviewerUpn.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            throw new ArgumentException("Reviewer identity is required.", nameof(reviewerUpn));
        return normalized;
    }
}
