using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Verifies the RSA signature on a <see cref="SignedPollResponseDto"/> returned by the server.
///
/// If <paramref name="publicKeyPem"/> is empty the check is skipped with a warning —
/// configure <c>ServerPublicKeyPem</c> in raizen-config.json for full MITM protection.
/// </summary>
public static class PollSignatureVerifier
{
    /// <summary>
    /// Returns <c>true</c> if the response is trusted (signature valid, or key not configured).
    /// Returns <c>false</c> if the signature is invalid — the caller must discard the response.
    /// </summary>
    public static bool Verify(SignedPollResponseDto response, string? publicKeyPem, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            logger.LogCritical(
                "ServerPublicKeyPem is not configured in raizen-config.json. " +
                "Poll responses REJECTED — configure the public key to enable request execution. " +
                "Copy the public key from the server log and add it to raizen-config.json.");
            return false;
        }

        if (string.IsNullOrEmpty(response.Payload) || string.IsNullOrEmpty(response.Signature))
        {
            logger.LogCritical(
                "Server returned a poll response with missing Payload or Signature. " +
                "The server may not have signing enabled yet, or the response was stripped. Rejecting.");
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(response.Payload);
            var sigBytes     = Convert.FromBase64String(response.Signature);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            if (rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return true;

            logger.LogCritical(
                "POLL RESPONSE SIGNATURE VERIFICATION FAILED. " +
                "A MITM may be injecting fake approvals. Rejecting {Count} request(s).",
                response.Requests.Count);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Exception during poll response signature verification. Rejecting response.");
            return false;
        }
    }

    /// <summary>
    /// After a successful <see cref="Verify"/>, parse the request list directly from the signed
    /// payload bytes rather than from the deserialized <see cref="SignedPollResponseDto.Requests"/>
    /// field, so an attacker cannot swap the requests array while keeping a valid signature.
    /// </summary>
    public static List<ElevationRequestDto> ParseRequests(
        SignedPollResponseDto response,
        System.Text.Json.JsonSerializerOptions opts)
    {
        var payloadBytes = Convert.FromBase64String(response.Payload);
        return System.Text.Json.JsonSerializer.Deserialize<List<ElevationRequestDto>>(payloadBytes, opts) ?? [];
    }

    /// <summary>
    /// Verifies a raw base64 payload + signature pair using the provided public key PEM.
    /// Used for signed agent version responses.
    /// Returns true if the signature is valid, false otherwise (logs the reason).
    /// </summary>
    public static bool VerifyPayload(
        string? payload,
        string? signature,
        string  publicKeyPem,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signature))
        {
            logger.LogCritical(
                "Server returned a version response with missing Payload or Signature. Rejecting.");
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(payload);
            var sigBytes     = Convert.FromBase64String(signature);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            if (rsa.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return true;

            logger.LogCritical(
                "VERSION RESPONSE SIGNATURE VERIFICATION FAILED. " +
                "A MITM may be tampering with update responses. Update aborted.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Exception during version response signature verification. Update aborted.");
            return false;
        }
    }
}
