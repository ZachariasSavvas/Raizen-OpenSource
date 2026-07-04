using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class AddTrustedCertificateHandlerTests
{
    private readonly AddTrustedCertificateHandler _handler = new(NullLogger<AddTrustedCertificateHandler>.Instance);

    private static ElevationRequestDto MakeRequest(Dictionary<string, string> parameters) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = ActionType.AddTrustedCertificate,
        Parameters = parameters,
    };

    private static string GenerateValidCertBase64()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=RaizenTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }

    private static string GenerateExpiredCertBase64()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=RaizenExpired", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }

    [Fact]
    public void HandledType_IsAddTrustedCertificate()
    {
        Assert.Equal(ActionType.AddTrustedCertificate, _handler.HandledType);
    }

    [Fact]
    public void Validate_MissingCertData_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>()));
        Assert.NotNull(error);
        Assert.Contains("CertificateBase64", error);
    }

    [Fact]
    public void Validate_InvalidBase64_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = "not-valid-base64!!!",
        }));
        Assert.NotNull(error);
        Assert.Contains("Base64", error);
    }

    [Fact]
    public void Validate_ValidCert_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = GenerateValidCertBase64(),
        }));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ExpiredCert_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = GenerateExpiredCertBase64(),
        }));
        Assert.NotNull(error);
        Assert.Contains("expired", error);
    }

    [Fact]
    public void Validate_InvalidStoreName_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = GenerateValidCertBase64(),
            ["StoreName"] = "Personal",
        }));
        Assert.NotNull(error);
        Assert.Contains("Root", error);
    }

    [Fact]
    public void Validate_TrustedPublisher_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = GenerateValidCertBase64(),
            ["StoreName"] = "TrustedPublisher",
        }));
        Assert.Null(error);
    }

    [Fact]
    public async Task Execute_InvalidBase64_ReturnsFailure()
    {
        var result = await _handler.ExecuteAsync(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = "not-base64",
        }), CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Execute_InvalidStoreName_ReturnsFailure()
    {
        var result = await _handler.ExecuteAsync(MakeRequest(new Dictionary<string, string>
        {
            ["CertificateBase64"] = GenerateValidCertBase64(),
            ["StoreName"] = "BadStore",
        }), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("not permitted", result.ErrorMessage);
    }
}
