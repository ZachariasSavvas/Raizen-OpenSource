using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;

namespace Raizen.Tests;

/// <summary>
/// Simple IDbContextFactory wrapper for unit tests using EF Core InMemory.
/// Each call to CreateDbContext returns a new context sharing the same named
/// in-memory database, so seeding done on a direct context is visible to
/// services that use the factory.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<RaizenDbContext> opts)
    : IDbContextFactory<RaizenDbContext>
{
    public RaizenDbContext CreateDbContext() => new(opts);

    public Task<RaizenDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
