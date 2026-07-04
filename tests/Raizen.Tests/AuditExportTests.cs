using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Services;

namespace Raizen.Tests;

/// <summary>Tests for AuditExportService — verifies the signed JSON export payload is valid and decodable.</summary>
public sealed class AuditExportTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly AuditService _audit;
    private readonly AuditExportService _export;

    public AuditExportTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"AuditExport_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(opts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Export"
            })
            .Build();

        var factory = new TestDbContextFactory(opts);
        _audit  = new AuditService(factory, new NullSyslogSender(), cfg);
        _export = new AuditExportService(factory);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Export_EmptyDb_PayloadIsValidJson()
    {
        var result = await _export.ExportAsync(null, null, null, null);

        Assert.False(string.IsNullOrEmpty(result.Payload));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(result.Payload));
        using var doc = JsonDocument.Parse(json); // throws if invalid
        Assert.Equal(0, result.RecordCount);
        Assert.Equal(0, doc.RootElement.GetProperty("recordCount").GetInt32());
    }

    [Fact]
    public async Task Export_WithRecords_RecordCountMatchesPayload()
    {
        await _audit.LogAsync("event.one",   "alice@corp.com");
        await _audit.LogAsync("event.two",   "bob@corp.com");
        await _audit.LogAsync("event.three", "carol@corp.com");

        var result = await _export.ExportAsync(null, null, null, null);

        Assert.Equal(3, result.RecordCount);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(result.Payload));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("recordCount").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task Export_Payload_Base64DecodesTo_WellFormedJson()
    {
        await _audit.LogAsync("test.event", "user@corp.com", detail: "some detail");

        var result = await _export.ExportAsync(null, null, null, null);

        // Simulate what blazorDownloadFile does: decode base64 → bytes → string
        var payloadBytes = Convert.FromBase64String(result.Payload);
        var json = Encoding.UTF8.GetString(payloadBytes);

        Assert.NotEmpty(json);
        Assert.StartsWith("{", json.TrimStart());

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("records", out var records));
        Assert.Equal(1, records.GetArrayLength());

        var first = records[0];
        Assert.Equal("test.event", first.GetProperty("event").GetString());
        Assert.Equal("user@corp.com", first.GetProperty("actorUpn").GetString());
    }

    [Fact]
    public async Task Export_WrapperJson_ContainsAllExpectedFields()
    {
        await _audit.LogAsync("test.event", "user@corp.com");

        var result = await _export.ExportAsync(null, null, null, null);

        // Simulate what AuditLogs.razor does: build wrapper JSON, encode to base64
        var wrapper = JsonSerializer.Serialize(new
        {
            Algorithm   = result.Algorithm,
            ExportedAt  = result.ExportedAt,
            RecordCount = result.RecordCount,
            Signature   = result.Signature,
            Payload     = result.Payload,
        }, new JsonSerializerOptions { WriteIndented = true });

        var wrapperBytes  = Encoding.UTF8.GetBytes(wrapper);
        var wrapperBase64 = Convert.ToBase64String(wrapperBytes);

        // Simulate what atob() + Uint8Array does: base64 → bytes → string
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(wrapperBase64));
        using var doc = JsonDocument.Parse(decoded);

        Assert.Equal("HMAC-SHA256", doc.RootElement.GetProperty("Algorithm").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("RecordCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("Signature").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("Payload").GetString()));
    }

    [Fact]
    public async Task Export_EventFilter_OnlyReturnsMatchingRecords()
    {
        await _audit.LogAsync("request.approved", "alice@corp.com");
        await _audit.LogAsync("auth.login",        "bob@corp.com");
        await _audit.LogAsync("request.denied",    "carol@corp.com");

        var result = await _export.ExportAsync("request", null, null, null);

        Assert.Equal(2, result.RecordCount);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(result.Payload));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("records").GetArrayLength());
    }
}
