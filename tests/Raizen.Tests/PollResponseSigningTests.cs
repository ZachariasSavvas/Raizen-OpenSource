using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>
/// Tests for the poll-response RSA signing / verification pipeline.
/// Covers: sign, tamper-payload, tamper-signature, wrong key, missing key, empty response.
/// </summary>
public sealed class PollResponseSigningTests
{
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

    private static List<ElevationRequestDto> MakeRequests(int count = 1) =>
        Enumerable.Range(0, count).Select(_ => new ElevationRequestDto
        {
            Id                 = Guid.NewGuid(),
            ActionDefinitionId = Guid.NewGuid(),
            ActionType         = ActionType.CopyFile,
            RequesterUpn       = "user@test.com",
            ActionDisplayName  = "Copy Test File",
            Parameters         = new() { ["SourcePath"] = @"C:\src\file.txt", ["DestinationPath"] = @"C:\dst\" },
            Status             = RequestStatus.Approved,
            SubmittedAt        = DateTimeOffset.UtcNow,
            ExpiresAt          = DateTimeOffset.UtcNow.AddHours(1),
        }).ToList();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── PollResponseSigner tests ──────────────────────────────────────────────

    [Fact]
    public void Sign_PayloadIsBase64EncodedJsonOfRequests()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var requests = MakeRequests(2);
        var signed   = signer.Sign(requests);

        var payloadBytes = Convert.FromBase64String(signed.Payload);
        var deserialized = JsonSerializer.Deserialize<List<ElevationRequestDto>>(payloadBytes, JsonOpts);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal(requests[0].Id, deserialized[0].Id);
        Assert.Equal(requests[1].Id, deserialized[1].Id);
    }

    [Fact]
    public void Sign_SignatureVerifiesWithMatchingPublicKey()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var signed = signer.Sign(MakeRequests());

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pub);

        var payloadBytes = Convert.FromBase64String(signed.Payload);
        var sigBytes     = Convert.FromBase64String(signed.Signature);
        Assert.True(rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Sign_PublicKeyPemMatchesPrivateKey()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        using var rsaPriv = RSA.Create();
        rsaPriv.ImportFromPem(priv);
        var expectedPub = rsaPriv.ExportSubjectPublicKeyInfoPem();

        Assert.Equal(expectedPub, signer.PublicKeyPem);
    }

    [Fact]
    public void Sign_EmptyRequestList_ProducesValidSignature()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var signed = signer.Sign([]);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pub);
        var payloadBytes = Convert.FromBase64String(signed.Payload);
        var sigBytes     = Convert.FromBase64String(signed.Signature);
        Assert.True(rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    // ── PollSignatureVerifier.Verify tests ────────────────────────────────────

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign(MakeRequests());

        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign(MakeRequests());

        // Flip a byte in the payload
        var bytes = Convert.FromBase64String(signed.Payload);
        bytes[0] ^= 0xFF;
        signed.Payload = Convert.ToBase64String(bytes);

        Assert.False(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign(MakeRequests());

        // Flip a byte in the signature
        var sigBytes = Convert.FromBase64String(signed.Signature);
        sigBytes[0] ^= 0xFF;
        signed.Signature = Convert.ToBase64String(sigBytes);

        Assert.False(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_WrongPublicKey_ReturnsFalse()
    {
        var (priv, _)    = GenerateKeyPair();
        var (_, wrongPub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign(MakeRequests());

        Assert.False(PollSignatureVerifier.Verify(signed, wrongPub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_EmptyPublicKeyPem_ReturnsFalse_FailClosed()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);
        var signed = signer.Sign(MakeRequests());

        // No public key configured → must reject (fail-closed, prevents MITM)
        Assert.False(PollSignatureVerifier.Verify(signed, "", NullLogger.Instance));
        Assert.False(PollSignatureVerifier.Verify(signed, null, NullLogger.Instance));
    }

    [Fact]
    public void Verify_MissingPayload_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        var signed = new SignedPollResponseDto { Payload = "", Signature = "abc", Requests = [] };

        Assert.False(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    [Fact]
    public void Verify_MissingSignature_ReturnsFalse()
    {
        var (_, pub) = GenerateKeyPair();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("[]"));
        var signed  = new SignedPollResponseDto { Payload = payload, Signature = "", Requests = [] };

        Assert.False(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
    }

    // ── PollSignatureVerifier.ParseRequests tests ─────────────────────────────

    [Fact]
    public void ParseRequests_ReturnsRequestsFromPayload_NotFromRequestsField()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var real   = MakeRequests(1);
        var signed = signer.Sign(real);

        // Poison the convenience Requests field — ParseRequests must ignore it
        signed.Requests = MakeRequests(99);

        var parsed = PollSignatureVerifier.ParseRequests(signed, JsonOpts);

        Assert.Single(parsed);
        Assert.Equal(real[0].Id, parsed[0].Id);
    }

    [Fact]
    public void ParseRequests_EmptyPayload_ReturnsEmptyList()
    {
        var (priv, _) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var signed = signer.Sign([]);
        var parsed = PollSignatureVerifier.ParseRequests(signed, JsonOpts);

        Assert.Empty(parsed);
    }

    // ── Integration: sign → verify → parse (full happy path) ─────────────────

    [Fact]
    public void FullPipeline_SignVerifyParse_ReturnsOriginalRequests()
    {
        var (priv, pub) = GenerateKeyPair();
        using var signer = CreateSigner(priv);

        var original = MakeRequests(3);
        var signed   = signer.Sign(original);

        Assert.True(PollSignatureVerifier.Verify(signed, pub, NullLogger.Instance));
        var parsed = PollSignatureVerifier.ParseRequests(signed, JsonOpts);

        Assert.Equal(3, parsed.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(original[i].Id, parsed[i].Id);
            Assert.Equal(original[i].RequesterUpn, parsed[i].RequesterUpn);
            Assert.Equal(original[i].ActionType, parsed[i].ActionType);
        }
    }
}
