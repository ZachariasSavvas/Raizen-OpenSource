using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Adds a trusted certificate to the local machine certificate store.
///
/// Required parameters:
///   CertificateBase64 — Base64-encoded certificate data (.cer / .crt)
///
/// Optional parameters:
///   StoreName — Target store: "Root" (default) or "TrustedPublisher"
///
/// Security: uses X509Store with .NET APIs, no shell invocation.
/// Only Root and TrustedPublisher stores are permitted.
/// </summary>
public sealed class AddTrustedCertificateHandler(ILogger<AddTrustedCertificateHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly HashSet<string> AllowedStores = new(StringComparer.OrdinalIgnoreCase)
    {
        "Root", "TrustedPublisher",
    };

    public ActionType HandledType => ActionType.AddTrustedCertificate;

    public string? Validate(ElevationRequestDto request)
    {
        var certData = request.Parameters.GetValueOrDefault("CertificateBase64", "");
        if (string.IsNullOrWhiteSpace(certData))
            return "CertificateBase64 parameter is required.";

        try
        {
            var bytes = Convert.FromBase64String(certData);
            using var cert = new X509Certificate2(bytes);
            if (cert.NotAfter < DateTime.UtcNow)
                return $"Certificate has expired ({cert.NotAfter:yyyy-MM-dd}).";
        }
        catch (FormatException)
        {
            return "CertificateBase64 is not valid Base64.";
        }
        catch
        {
            return "CertificateBase64 does not contain a valid certificate.";
        }

        var storeName = request.Parameters.GetValueOrDefault("StoreName", "Root");
        if (!AllowedStores.Contains(storeName))
            return $"StoreName must be one of: {string.Join(", ", AllowedStores)}";

        return null;
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var certData = request.Parameters.GetValueOrDefault("CertificateBase64", "");
        var storeNameStr = request.Parameters.GetValueOrDefault("StoreName", "Root");

        if (!AllowedStores.Contains(storeNameStr))
            return Task.FromResult(new ActionResult(false, ErrorMessage: $"StoreName '{storeNameStr}' is not permitted."));

        byte[] certBytes;
        try
        {
            certBytes = Convert.FromBase64String(certData);
        }
        catch
        {
            return Task.FromResult(new ActionResult(false, ErrorMessage: "CertificateBase64 is not valid Base64."));
        }

        try
        {
            using var cert = new X509Certificate2(certBytes);

            if (cert.NotAfter < DateTime.UtcNow)
                return Task.FromResult(new ActionResult(false,
                    ErrorMessage: $"Certificate has expired ({cert.NotAfter:yyyy-MM-dd})."));

            var storeName = storeNameStr.Equals("TrustedPublisher", StringComparison.OrdinalIgnoreCase)
                ? StoreName.TrustedPublisher
                : StoreName.Root;

            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);

            // Check if certificate already exists
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
            if (existing.Count > 0)
            {
                log.LogInformation(
                    "[Request:{RequestId}] Certificate {Thumbprint} already exists in {Store}",
                    request.Id, cert.Thumbprint, storeNameStr);
                return Task.FromResult(new ActionResult(true,
                    ResultMessage: $"Certificate '{cert.Subject}' (thumbprint: {cert.Thumbprint[..16]}...) already exists in {storeNameStr}."));
            }

            store.Add(cert);

            log.LogInformation(
                "[Request:{RequestId}] Added certificate {Subject} ({Thumbprint}) to {Store}",
                request.Id, cert.Subject, cert.Thumbprint, storeNameStr);

            return Task.FromResult(new ActionResult(true,
                ResultMessage: $"Added certificate '{cert.Subject}' (thumbprint: {cert.Thumbprint[..16]}...) to {storeNameStr} store."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] AddTrustedCertificate failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Failed to add certificate. See endpoint logs for details."));
        }
    }
}
