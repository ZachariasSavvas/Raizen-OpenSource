using Raizen.Shared.DTOs;

namespace Raizen.Server.Core.Services;

public interface IBulkOperationService
{
    Task<BulkOperationDto> SubmitAsync(SubmitBulkOperationDto dto, string createdByUpn, CancellationToken ct = default);
    Task<BulkOperationDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<BulkOperationDto>> ListAsync(int page, int pageSize, CancellationToken ct = default);
}
