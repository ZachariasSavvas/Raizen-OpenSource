namespace Raizen.Shared.DTOs;

/// <summary>
/// Returned by GET /api/v1/requests/pending-execution.
///
/// The server RSA-signs <see cref="Payload"/> so the endpoint can detect MITM-injected
/// responses even over plain HTTP or when a rogue CA is in play.
///
/// Trust model:
///   - <see cref="Payload"/> is the canonical UTF-8 JSON of the requests array.
///   - <see cref="Signature"/> is the RSA-PKCS1-SHA256 signature over those exact bytes.
///   - The endpoint MUST verify the signature and parse requests from <see cref="Payload"/>,
///     not from the <see cref="Requests"/> convenience field.
/// </summary>
public sealed class SignedPollResponseDto
{
    /// <summary>
    /// Base64-encoded UTF-8 JSON of the requests array.
    /// This is the exact byte sequence that was signed by the server.
    /// Always deserialize requests from this field for authoritative data.
    /// </summary>
    public string Payload { get; set; } = "";

    /// <summary>Base64-encoded RSA-PKCS1-SHA256 signature over <see cref="Payload"/> bytes.</summary>
    public string Signature { get; set; } = "";

    /// <summary>
    /// Convenience field — deserialized from <see cref="Payload"/> by the server.
    /// Do NOT use this for execution decisions; always re-parse from <see cref="Payload"/>
    /// after signature verification.
    /// </summary>
    public List<ElevationRequestDto> Requests { get; set; } = [];

    /// <summary>
    /// When true, an admin has requested an immediate agent update.
    /// The endpoint should trigger an update check without waiting for the next scheduled interval.
    /// This field is NOT covered by the RSA signature (it is a hint, not a security-critical approval).
    /// </summary>
    public bool UpdateNow { get; set; }
}
