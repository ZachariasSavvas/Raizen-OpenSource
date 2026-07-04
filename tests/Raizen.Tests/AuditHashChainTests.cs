using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;

namespace Raizen.Tests;

/// <summary>Tests for the audit log HMAC hash chain tamper detection.</summary>
public sealed class AuditHashChainTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly AuditService _svc;

    public AuditHashChainTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"AuditChain_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(opts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_NotASecret_TestOnly"
            })
            .Build();

        _svc = new AuditService(new TestDbContextFactory(opts), new NullSyslogSender(), cfg);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var entry = new AuditLog
        {
            Id           = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Event        = "test.event",
            ActorUpn     = "user@corp.com",
            OccurredAt   = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PreviousHash = string.Empty,
        };

        var h1 = AuditService.ComputeHash(entry, "key");
        var h2 = AuditService.ComputeHash(entry, "key");

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // 32 bytes hex = 64 chars
    }

    [Fact]
    public void ComputeHash_DifferentKey_DifferentHash()
    {
        var entry = new AuditLog
        {
            Id         = Guid.NewGuid(),
            Event      = "test",
            ActorUpn   = "user@corp.com",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        var h1 = AuditService.ComputeHash(entry, "key1");
        var h2 = AuditService.ComputeHash(entry, "key2");

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public async Task VerifyChain_EmptyDb_ReturnsZeroTotal()
    {
        var (total, invalid) = await _svc.VerifyChainAsync();
        Assert.Equal(0, total);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public async Task VerifyChain_ValidChain_ReturnsZeroInvalid()
    {
        await _svc.LogAsync("event.one",   "alice@corp.com");
        await _svc.LogAsync("event.two",   "bob@corp.com");
        await _svc.LogAsync("event.three", "carol@corp.com");

        var (total, invalid) = await _svc.VerifyChainAsync();

        Assert.Equal(3, total);
        Assert.Equal(0, invalid);
    }

    [Fact]
    public async Task VerifyChain_TamperedEntry_ReturnsInvalidCount()
    {
        await _svc.LogAsync("event.one",   "alice@corp.com");
        await _svc.LogAsync("event.two",   "bob@corp.com");
        await _svc.LogAsync("event.three", "carol@corp.com");

        // Directly tamper with an entry (simulating direct DB manipulation)
        var entry = await _db.AuditLogs.FirstAsync(x => x.Event == "event.two");
        entry.Detail = "TAMPERED";
        await _db.SaveChangesAsync();

        var (total, invalid) = await _svc.VerifyChainAsync();

        Assert.Equal(3, total);
        Assert.True(invalid > 0, "Should detect at least one broken entry after tampering.");
    }

    [Fact]
    public async Task HashChain_FirstEntry_HasEmptyPreviousHash()
    {
        await _svc.LogAsync("event.first", "alice@corp.com");

        var entry = await _db.AuditLogs.SingleAsync();
        Assert.Equal(string.Empty, entry.PreviousHash);
        Assert.NotNull(entry.RowHash);
    }

    [Fact]
    public async Task HashChain_SecondEntry_PreviousHashMatchesFirstRowHash()
    {
        await _svc.LogAsync("event.one", "alice@corp.com");
        await _svc.LogAsync("event.two", "bob@corp.com");

        var entries = await _db.AuditLogs
            .OrderBy(x => x.OccurredAt)
            .ToListAsync();

        Assert.Equal(entries[0].RowHash, entries[1].PreviousHash);
    }
}
