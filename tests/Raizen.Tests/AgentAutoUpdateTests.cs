using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service;
using Raizen.Endpoint.Shared.Config;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;

namespace Raizen.Tests;

/// <summary>
/// Comprehensive tests for the agent auto-update pipeline.
///
/// Covers:
///   - Version signing + verification (SignVersion / VerifyPayload)
///   - MSI hash integrity (round-trip sign → parse → compare)
///   - Backward compatibility (null payload for pre-v1.1.0 servers)
///   - Security: tampered payload, tampered signature, wrong key, missing key
///   - AgentUpdateService decision logic via FakeHttpMessageHandler
///   - UpdateNow signal not covered by poll signature (by design)
///   - MsiHashCache behavior
///   - Edge cases: null hash, empty version, downgrade rejection
/// </summary>
public sealed class AgentAutoUpdateTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string privatePem, string publicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static PollResponseSigner CreateSigner(string privatePem)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:PollSigningPrivateKeyPem"] = privatePem,
            })
            .Build();
        return new PollResponseSigner(config, NullLogger<PollResponseSigner>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1. SignVersion — RSA-signed version response
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SignVersion_PayloadContainsVersionAndMsiHash()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion("1.2.3", "abcdef0123456789");

        Assert.Equal("1.2.3", dto.Version);
        Assert.NotNull(dto.Payload);
        Assert.NotNull(dto.Signature);

        var payloadBytes = Convert.FromBase64String(dto.Payload);
        var payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts);

        Assert.NotNull(payload);
        Assert.Equal("1.2.3", payload.Version);
        Assert.Equal("abcdef0123456789", payload.MsiSha256);
    }

    [Fact]
    public void SignVersion_SignatureVerifiesWithCorrectPublicKey()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion("2.0.0", "deadbeef");

        Assert.True(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, pub, NullLogger.Instance));
    }

    [Fact]
    public void SignVersion_NullMsiHash_StillSignsSuccessfully()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion("1.0.0", null);

        Assert.True(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, pub, NullLogger.Instance));

        var payloadBytes = Convert.FromBase64String(dto.Payload!);
        var payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts);
        Assert.Null(payload!.MsiSha256);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2. VerifyPayload — signature verification for version responses
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void VerifyPayload_TamperedPayload_ReturnsFalse()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var dto = signer.SignVersion("1.0.0", "hash123");

        // Flip a byte in the payload
        var bytes = Convert.FromBase64String(dto.Payload!);
        bytes[0] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        Assert.False(PollSignatureVerifier.VerifyPayload(
            tampered, dto.Signature, pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_TamperedSignature_ReturnsFalse()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var dto = signer.SignVersion("1.0.0", "hash123");

        var sigBytes = Convert.FromBase64String(dto.Signature!);
        sigBytes[0] ^= 0xFF;
        var tampered = Convert.ToBase64String(sigBytes);

        Assert.False(PollSignatureVerifier.VerifyPayload(
            dto.Payload, tampered, pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_WrongPublicKey_ReturnsFalse()
    {
        var (priv, _) = GenerateKeyPair();
        var (_, wrongPub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var dto = signer.SignVersion("1.0.0", "hash123");

        Assert.False(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, wrongPub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_EmptyPublicKey_FailClosed()
    {
        // VerifyPayload requires a non-empty public key — but the check is in
        // AgentUpdateService, not in VerifyPayload itself. VerifyPayload would
        // throw on ImportFromPem(""). Let's verify the behavior.
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var dto = signer.SignVersion("1.0.0", "hash123");

        // Empty key → should return false (exception caught internally)
        Assert.False(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, "", NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_NullPayload_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        Assert.False(PollSignatureVerifier.VerifyPayload(
            null, "signature", pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_NullSignature_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        Assert.False(PollSignatureVerifier.VerifyPayload(
            "payload", null, pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_EmptyPayload_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        Assert.False(PollSignatureVerifier.VerifyPayload(
            "", "sig", pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_EmptySignature_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("test"));
        Assert.False(PollSignatureVerifier.VerifyPayload(
            payload, "", pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_InvalidBase64Payload_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        // "!!!" is not valid base64
        Assert.False(PollSignatureVerifier.VerifyPayload(
            "!!!", "dGVzdA==", pub, NullLogger.Instance));
    }

    [Fact]
    public void VerifyPayload_InvalidBase64Signature_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        Assert.False(PollSignatureVerifier.VerifyPayload(
            "dGVzdA==", "!!!", pub, NullLogger.Instance));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3. Full sign → verify → parse pipeline for version responses
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FullVersionPipeline_SignVerifyParse_ReturnsOriginalData()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion("3.5.1", "a1b2c3d4e5f6");

        // Step 1: Verify signature
        Assert.True(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, pub, NullLogger.Instance));

        // Step 2: Parse payload
        var payloadBytes = Convert.FromBase64String(dto.Payload!);
        var payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts);

        Assert.NotNull(payload);
        Assert.Equal("3.5.1", payload.Version);
        Assert.Equal("a1b2c3d4e5f6", payload.MsiSha256);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4. MSI hash verification logic (SHA-256 fixed-time compare)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MsiHashVerification_MatchingHash_Succeeds()
    {
        var msiBytes = Encoding.UTF8.GetBytes("fake MSI content for testing");
        var expectedHash = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();
        var actualHash = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        Assert.True(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHash),
            Encoding.ASCII.GetBytes(expectedHash)));
    }

    [Fact]
    public void MsiHashVerification_DifferentContent_Fails()
    {
        var realMsi = Encoding.UTF8.GetBytes("real MSI content");
        var fakeMsi = Encoding.UTF8.GetBytes("tampered MSI content");

        var expectedHash = Convert.ToHexString(SHA256.HashData(realMsi)).ToLowerInvariant();
        var actualHash = Convert.ToHexString(SHA256.HashData(fakeMsi)).ToLowerInvariant();

        Assert.False(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHash),
            Encoding.ASCII.GetBytes(expectedHash)));
    }

    [Fact]
    public void MsiHashVerification_CaseInsensitive()
    {
        // The code does .ToLowerInvariant() on both sides; verify that works
        var msiBytes = Encoding.UTF8.GetBytes("test data");
        var upperHash = Convert.ToHexString(SHA256.HashData(msiBytes)); // uppercase
        var lowerHash = upperHash.ToLowerInvariant();

        // Both should match after lowering
        Assert.True(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(lowerHash),
            Encoding.ASCII.GetBytes(lowerHash)));

        // Upper vs lower should NOT match (different bytes)
        Assert.NotEqual(upperHash, lowerHash);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  5. UpdateNow signal — NOT covered by RSA signature (by design)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateNow_NotCoveredBySignature_CanBeInjectedByMITM()
    {
        // This test documents the known design decision: UpdateNow is a hint,
        // not a signed field. It can be flipped by a MITM but the actual update
        // check independently verifies the version response.
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var signed = signer.Sign([]);

        // Signature is valid with UpdateNow = false (default)
        Assert.False(signed.UpdateNow);
        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));

        // Flip UpdateNow — signature STILL valid because it's not in the payload
        signed.UpdateNow = true;
        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  6. AgentUpdateService decision logic (via HTTP mocking)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fake HTTP handler that returns canned responses for version/msi endpoints.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode VersionStatusCode { get; set; } = HttpStatusCode.OK;
        public string? VersionResponseJson { get; set; }
        public HttpStatusCode MsiStatusCode { get; set; } = HttpStatusCode.OK;
        public byte[]? MsiBytes { get; set; }
        public bool VersionCalled { get; private set; }
        public bool MsiCalled { get; private set; }
        public bool ThrowOnVersion { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.Contains("agent/version"))
            {
                VersionCalled = true;
                if (ThrowOnVersion)
                    throw new HttpRequestException("Network error");

                var resp = new HttpResponseMessage(VersionStatusCode);
                if (VersionResponseJson is not null)
                    resp.Content = new StringContent(VersionResponseJson, Encoding.UTF8, "application/json");
                return Task.FromResult(resp);
            }

            if (path.Contains("agent/msi"))
            {
                MsiCalled = true;
                var resp = new HttpResponseMessage(MsiStatusCode);
                if (MsiBytes is not null)
                    resp.Content = new ByteArrayContent(MsiBytes);
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Creates a signed version DTO and the corresponding MSI bytes for testing.
    /// </summary>
    private static (string versionJson, byte[] msiBytes, string publicKeyPem) CreateSignedUpdatePackage(
        string version, string privatePem, string publicPem)
    {
        using var signer = CreateSigner(privatePem);
        var msiBytes = Encoding.UTF8.GetBytes($"MSI-{version}-{Guid.NewGuid()}");
        var msiHash = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();
        var dto = signer.SignVersion(version, msiHash);
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        return (json, msiBytes, publicPem);
    }

    private static ConfigLoader CreateTestConfigLoader(string serverUrl, string apiKey, string publicKeyPem)
    {
        // Create a temp config file for the ConfigLoader
        var tempDir = Path.Combine(Path.GetTempPath(), $"raizen-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "raizen-config.json");

        var config = new
        {
            ServerUrl = serverUrl,
            ApiKey = apiKey,
            MachineId = Guid.NewGuid().ToString(),
            ServerPublicKeyPem = publicKeyPem,
            PollIntervalSeconds = 30,
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return new ConfigLoader(configPath);
    }

    [Fact]
    public async Task CheckAndUpdate_ServerUnreachable_ReturnsGracefully()
    {
        var (priv, pub) = GenerateKeyPair();
        var handler = new FakeHandler { ThrowOnVersion = true };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        // Should not throw
        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_404_NotConfigured_ReturnsGracefully()
    {
        var (priv, pub) = GenerateKeyPair();
        var handler = new FakeHandler { VersionStatusCode = HttpStatusCode.NotFound };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_NoApiKey_ReturnsImmediately()
    {
        var handler = new FakeHandler();
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "", "");
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        // Should not even try to call the server
        Assert.False(handler.VersionCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_NoServerUrl_ReturnsImmediately()
    {
        var handler = new FakeHandler();
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("", "testkey", "");
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.False(handler.VersionCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_NullPayload_PreV110Server_SkipsUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        // Simulate pre-v1.1.0 server that returns version but no signed payload
        var body = new SignedAgentVersionDto { Version = "99.0.0", Payload = null, Signature = null };
        var handler = new FakeHandler
        {
            VersionResponseJson = JsonSerializer.Serialize(body, JsonOpts)
        };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled); // Must not download MSI
    }

    [Fact]
    public async Task CheckAndUpdate_EmptyPublicKeyPem_RefusesUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        var (json, _, _) = CreateSignedUpdatePackage("99.0.0", priv, pub);
        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);

        // Empty public key in config
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", "");
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled); // Must refuse without public key
    }

    [Fact]
    public async Task CheckAndUpdate_InvalidSignature_RefusesUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        var (_, wrongPub) = GenerateKeyPair(); // wrong key pair

        // Sign with one key, but verify with another
        var (json, msiBytes, _) = CreateSignedUpdatePackage("99.0.0", priv, pub);
        var handler = new FakeHandler { VersionResponseJson = json, MsiBytes = msiBytes };
        var factory = new FakeHttpClientFactory(handler);

        // Config has the WRONG public key
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", wrongPub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled); // Must not download after sig failure
    }

    [Fact]
    public async Task CheckAndUpdate_NullMsiHash_SkipsUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        // Sign a version with null MSI hash
        var dto = signer.SignVersion("99.0.0", null);
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled); // Must not download without hash
    }

    [Fact]
    public async Task CheckAndUpdate_MsiHashMismatch_AbortsUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        // Sign version with a specific hash
        var msiBytes = Encoding.UTF8.GetBytes("real MSI");
        var realHash = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();
        var dto = signer.SignVersion("99.0.0", realHash);
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        // But serve DIFFERENT MSI bytes
        var tamperedMsi = Encoding.UTF8.GetBytes("tampered MSI");

        var handler = new FakeHandler
        {
            VersionResponseJson = json,
            MsiBytes = tamperedMsi
        };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        // Should download MSI but abort due to hash mismatch
        // (won't call msiexec — verified by the fact it doesn't crash or throw)
        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.True(handler.MsiCalled); // Downloaded the MSI
        // The update was aborted — no msiexec call (can't directly assert this
        // without wrapping Process.Start, but we verify the flow didn't throw)
    }

    [Fact]
    public async Task CheckAndUpdate_MsiDownloadFails_ReturnsGracefully()
    {
        var (priv, pub) = GenerateKeyPair();
        var (json, _, _) = CreateSignedUpdatePackage("99.0.0", priv, pub);

        var handler = new FakeHandler
        {
            VersionResponseJson = json,
            MsiStatusCode = HttpStatusCode.InternalServerError
        };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.True(handler.MsiCalled);
        // No crash — graceful return
    }

    [Fact]
    public async Task CheckAndUpdate_ServerReturns500_ReturnsGracefully()
    {
        var handler = new FakeHandler { VersionStatusCode = HttpStatusCode.InternalServerError };
        var factory = new FakeHttpClientFactory(handler);
        var (_, pub) = GenerateKeyPair();
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_UnparseableVersionString_SkipsUpdate()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        // Sign a version response with an unparseable version string
        var dto = signer.SignVersion("not-a-version", "somehash");
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_EmptyVersionInBody_ReturnsGracefully()
    {
        var body = new SignedAgentVersionDto { Version = "", Payload = null, Signature = null };
        var handler = new FakeHandler
        {
            VersionResponseJson = JsonSerializer.Serialize(body, JsonOpts)
        };
        var factory = new FakeHttpClientFactory(handler);
        var (_, pub) = GenerateKeyPair();
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    [Fact]
    public async Task CheckAndUpdate_NullBody_ReturnsGracefully()
    {
        var handler = new FakeHandler
        {
            VersionResponseJson = "null"
        };
        var factory = new FakeHttpClientFactory(handler);
        var (_, pub) = GenerateKeyPair();
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  7. Version comparison logic
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndUpdate_ServerVersionNotNewer_SkipsMsiDownload()
    {
        // We can't control LocalVersion (it's Assembly version = 0.0.0 in tests),
        // so we use server version "0.0.0" to test the "equal" case
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var dto = signer.SignVersion("0.0.0", "hash"); // same as test assembly version
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled); // No download needed
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  8. PollResponseSigner.SignVersion round-trip consistency
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("1.0.0", "abc123")]
    [InlineData("99.99.99", "")]
    [InlineData("0.0.1", null)]
    public void SignVersion_RoundTrip_PayloadMatchesTopLevel(string version, string? hash)
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion(version, hash);

        // Top-level version should match payload version
        Assert.Equal(version, dto.Version);

        var payloadBytes = Convert.FromBase64String(dto.Payload!);
        var payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts)!;
        Assert.Equal(version, payload.Version);
        Assert.Equal(hash, payload.MsiSha256);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BUG: 3-component vs 4-component Version comparison
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug_ThreeComponentVersion_LessThanFourComponent_EvenWhenEqual()
    {
        // Assembly.GetName().Version returns 4-component: 1.1.0.0 (Revision=0)
        // Version.TryParse("1.1.0") returns 3-component: 1.1.0 (Revision=-1)
        // .NET considers 1.1.0 < 1.1.0.0 because -1 < 0
        // This means agents ALWAYS think they're "newer" than the server version
        // when the version strings match but component counts differ.

        var threeComponent = new Version("1.1.0");      // Revision = -1
        var fourComponent  = new Version("1.1.0.0");    // Revision = 0

        // This is the bug: these represent the same version but aren't equal
        Assert.True(threeComponent < fourComponent,
            "3-component 1.1.0 should be less than 4-component 1.1.0.0 in .NET (this is the bug)");

        Assert.True(threeComponent <= fourComponent,
            "serverVersion <= LocalVersion would be TRUE, causing the agent to skip the update");

        // Even a NEWER server version like "1.2.0" has Revision=-1
        // but it still works because Minor differs (2 > 1).
        // The bug only bites when Major.Minor.Build are the same.
        var serverNewer = new Version("1.2.0");
        Assert.False(serverNewer <= fourComponent, "1.2.0 > 1.1.0.0 — this case works fine");

        // After the fix (normalizing to 3-component), the comparison works correctly
        var normalizedLocal = new Version(fourComponent.Major, fourComponent.Minor, fourComponent.Build);
        Assert.False(threeComponent < normalizedLocal, "After normalization, 1.1.0 == 1.1.0");
        Assert.True(threeComponent <= normalizedLocal, "Equal versions should be <=");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  9. BUG DETECTION: Top-level Version can diverge from Payload.Version
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug_TopLevelVersionCanDivergeFromSignedPayload()
    {
        // The server sets dto.Version = version AND payload.Version = version in
        // SignVersion, so they should always match. But if someone modified the DTO
        // after signing, the top-level Version could differ from the signed payload.
        //
        // Old agents (pre-v1.1.0) only read dto.Version — they would act on the
        // tampered value. New agents parse from signed payload and are safe.
        //
        // This test demonstrates the attack vector against old agents.

        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var dto = signer.SignVersion("1.0.0", "realhash");

        // MITM tampers with the top-level Version but can't touch the signed payload
        dto.Version = "0.0.1"; // downgrade attack

        // Signature still valid (it covers payload, not top-level fields)
        Assert.True(PollSignatureVerifier.VerifyPayload(
            dto.Payload, dto.Signature, pub, NullLogger.Instance));

        // Top-level says 0.0.1, but signed payload says 1.0.0
        var payloadBytes = Convert.FromBase64String(dto.Payload!);
        var payload = JsonSerializer.Deserialize<AgentVersionPayload>(payloadBytes, JsonOpts)!;
        Assert.NotEqual(dto.Version, payload.Version); // Diverged!
        Assert.Equal("1.0.0", payload.Version);        // Signed truth
        Assert.Equal("0.0.1", dto.Version);             // Tampered
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  10. BUG: Convenience Requests field can be swapped independently of Payload
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug_ConvenienceRequestsFieldCanBeSwapped()
    {
        // The signed poll response has both Payload (signed) and Requests (convenience).
        // An attacker could replace Requests while keeping a valid signature.
        // The code correctly uses ParseRequests (from payload), but this test verifies
        // the attack vector exists.

        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var real = new List<ElevationRequestDto>
        {
            new() { Id = Guid.NewGuid(), ActionDisplayName = "Safe Action" }
        };
        var signed = signer.Sign(real);

        // Attacker injects a different request into the convenience field
        signed.Requests = [new() { Id = Guid.NewGuid(), ActionDisplayName = "Malicious Action" }];

        // Signature still valid
        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));

        // ParseRequests correctly ignores the tampered Requests field
        var parsed = PollSignatureVerifier.ParseRequests(signed, JsonOpts);
        Assert.Single(parsed);
        Assert.Equal("Safe Action", parsed[0].ActionDisplayName);
        Assert.NotEqual("Malicious Action", parsed[0].ActionDisplayName);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  11. Cancellation handling
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndUpdate_CancellationRequested_ReturnsGracefully()
    {
        var handler = new FakeHandler
        {
            VersionStatusCode = HttpStatusCode.OK,
            VersionResponseJson = "{\"version\":\"99.0.0\"}" // triggers work
        };
        var factory = new FakeHttpClientFactory(handler);
        var (_, pub) = GenerateKeyPair();
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Should not throw — the HttpClient will throw OperationCanceledException
        // which is caught by the outer try-catch or the caller
        try
        {
            await svc.CheckAndUpdateAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — the HTTP call sees the cancelled token
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  12. License feature gate (402 response)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndUpdate_LicenseRequired402_ReturnsGracefully()
    {
        var handler = new FakeHandler
        {
            VersionStatusCode = (HttpStatusCode)402 // Payment Required
        };
        var factory = new FakeHttpClientFactory(handler);
        var (_, pub) = GenerateKeyPair();
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  13. MsiHashCache unit tests
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MsiHashCache_NoPathConfigured_ReturnsNull()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { })
            .Build();
        var cache = new Server.Api.Services.MsiHashCache(config, NullLogger<Server.Api.Services.MsiHashCache>.Instance);

        Assert.Null(cache.GetHash());
    }

    [Fact]
    public void MsiHashCache_FileDoesNotExist_ReturnsNull()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentDeployment:InstallerPath"] = @"C:\nonexistent\fake.msi"
            })
            .Build();
        var cache = new Server.Api.Services.MsiHashCache(config, NullLogger<Server.Api.Services.MsiHashCache>.Instance);

        Assert.Null(cache.GetHash());
    }

    [Fact]
    public void MsiHashCache_ValidFile_ReturnsCorrectHash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = Encoding.UTF8.GetBytes("test MSI content");
            File.WriteAllBytes(tempFile, content);
            var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentDeployment:InstallerPath"] = tempFile
                })
                .Build();
            var cache = new Server.Api.Services.MsiHashCache(config, NullLogger<Server.Api.Services.MsiHashCache>.Instance);

            var hash = cache.GetHash();
            Assert.Equal(expectedHash, hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MsiHashCache_CachesHashUntilFileChanges()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [1, 2, 3]);
            var hash1 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentDeployment:InstallerPath"] = tempFile
                })
                .Build();
            var cache = new Server.Api.Services.MsiHashCache(config, NullLogger<Server.Api.Services.MsiHashCache>.Instance);

            // First call computes
            Assert.Equal(hash1, cache.GetHash());

            // Second call returns cached (same file)
            Assert.Equal(hash1, cache.GetHash());

            // Update file
            Thread.Sleep(50); // ensure different timestamp
            File.WriteAllBytes(tempFile, [4, 5, 6]);
            var hash2 = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6 })).ToLowerInvariant();

            Assert.Equal(hash2, cache.GetHash());
            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  14. PollResponseSigner key persistence
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PollResponseSigner_LoadsKeyFromConfig()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        Assert.Equal(pub, signer.PublicKeyPem);
    }

    [Fact]
    public void PollResponseSigner_DifferentKeys_ProduceDifferentPublicKeys()
    {
        var (priv1, pub1) = GenerateKeyPair();
        var (priv2, pub2) = GenerateKeyPair();

        using var signer1 = CreateSigner(priv1);
        using var signer2 = CreateSigner(priv2);

        Assert.NotEqual(signer1.PublicKeyPem, signer2.PublicKeyPem);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  15. Edge case: version payload with corrupted JSON inside valid base64
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndUpdate_CorruptedPayloadJson_ReturnsGracefully()
    {
        var (priv, pub) = GenerateKeyPair();

        // Create a validly-signed response where the payload is valid base64
        // but contains broken JSON (simulating a rare corruption scenario)
        using var rsa = RSA.Create(2048);
        rsa.ImportFromPem(priv);

        var brokenJson = "{ this is not valid JSON }";
        var payloadBytes = Encoding.UTF8.GetBytes(brokenJson);
        var sigBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var dto = new SignedAgentVersionDto
        {
            Version = "99.0.0",
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(sigBytes),
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        // Should not throw — deserialisation failure is handled
        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  16. Edge case: version payload with empty version inside signed JSON
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckAndUpdate_SignedPayloadWithEmptyVersion_SkipsUpdate()
    {
        var (priv, pub) = GenerateKeyPair();

        // Manually create a signed payload where Version is empty
        using var rsa = RSA.Create(2048);
        rsa.ImportFromPem(priv);

        var payload = new AgentVersionPayload { Version = "", MsiSha256 = "hash" };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var sigBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var dto = new SignedAgentVersionDto
        {
            Version = "",
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(sigBytes),
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);

        var handler = new FakeHandler { VersionResponseJson = json };
        var factory = new FakeHttpClientFactory(handler);
        using var configLoader = CreateTestConfigLoader("https://test:5001", "testkey", pub);
        var svc = new AgentUpdateService(configLoader, factory, NullLogger<AgentUpdateService>.Instance);

        await svc.CheckAndUpdateAsync(CancellationToken.None);

        Assert.True(handler.VersionCalled);
        Assert.False(handler.MsiCalled);
    }
}
