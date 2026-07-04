using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;


namespace Raizen.Server.Core.Services;

public interface IRequestService
{
    Task<ElevationRequestDto> SubmitAsync(
        SubmitElevationRequestDto dto,
        string requesterUpn,
        string requesterDisplayName,
        Guid endpointRegistrationId,
        CancellationToken ct = default);

    Task<ElevationRequestDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<ElevationRequestDto>> ListAsync(
        RequestStatus? status,
        string? requesterUpn,
        string? machineId,
        int page,
        int pageSize,
        CancellationToken ct = default,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null);

    /// <summary>Returns the count of requests currently in Pending status.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>Returns approved requests pending execution for the given endpoint.</summary>
    Task<List<ElevationRequestDto>> GetPendingExecutionAsync(
        Guid endpointRegistrationId,
        CancellationToken ct = default);

    Task<ElevationRequestDto> ReviewAsync(
        Guid requestId,
        ReviewRequestDto review,
        string reviewerUpn,
        CancellationToken ct = default);

    Task<ElevationRequestDto> CancelAsync(
        Guid requestId,
        string requesterUpn,
        CancellationToken ct = default);

    /// <summary>
    /// Reports execution result. Verifies the request belongs to the calling endpoint.
    /// </summary>
    Task<ElevationRequestDto> ReportExecutionResultAsync(
        ExecutionResultDto result,
        Guid callerRegistrationId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions an Approved request to Executing.
    /// Verifies the request belongs to the calling endpoint.
    /// Uses optimistic concurrency so two endpoints cannot double-dispatch the same request.
    /// Returns null if the request is no longer in Approved status, has expired, or belongs to a different endpoint.
    /// </summary>
    Task<ElevationRequestDto?> MarkAsExecutingAsync(Guid requestId, Guid callerRegistrationId, CancellationToken ct = default);

    /// <summary>Marks stale Approved requests as Expired. Called by a background job.</summary>
    Task ExpireStaleRequestsAsync(CancellationToken ct = default);

    Task<List<RequestCommentDto>> GetCommentsAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Returns individual approver votes for a request (dual-approval audit trail).</summary>
    Task<List<RequestApprovalDto>> GetApprovalsAsync(Guid requestId, CancellationToken ct = default);

    Task<RequestCommentDto> AddCommentAsync(
        Guid requestId,
        AddCommentDto dto,
        string authorUpn,
        string authorDisplayName,
        bool isAdmin,
        CancellationToken ct = default);
}
