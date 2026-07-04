namespace Raizen.Shared.DTOs;

/// <summary>
/// Returned by GET /api/v1/agent/version.
///
/// Backward-compatible with older agents that only deserialise the top-level
/// <see cref="Version"/> field and ignore everything else.
///
/// New agents (v1.1.0+) verify the RSA signature over <see cref="Payload"/> and
/// use the embedded <see cref="AgentVersionPayload.MsiSha256"/> to verify the MSI
/// download before running msiexec.
///
/// Trust model:
///   - <see cref="Payload"/> is the canonical UTF-8 JSON of <see cref="AgentVersionPayload"/>.
///   - <see cref="Signature"/> is the RSA-PKCS1-SHA256 signature over those exact bytes.
///   - New agents MUST parse from <see cref="Payload"/> after signature verification,
///     not from any convenience field.
///   - If <see cref="Payload"/> is null the server is pre-v1.1.0 and does not support
///     verified updates — new agents must skip the update in that case.
/// </summary>
public sealed class SignedAgentVersionDto
{
    /// <summary>
    /// The current agent version string. Present at the top level so that
    /// pre-v1.1.0 agents (which only read this field) continue to work.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded UTF-8 JSON of <see cref="AgentVersionPayload"/>.
    /// This is the exact byte sequence that was signed by the server.
    /// Null when the server does not support signed version responses.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Base64-encoded RSA-PKCS1-SHA256 signature over <see cref="Payload"/> bytes.
    /// Null when <see cref="Payload"/> is null.
    /// </summary>
    public string? Signature { get; set; }
}

/// <summary>
/// The strongly-typed content of <see cref="SignedAgentVersionDto.Payload"/>.
/// Always parse from the verified payload bytes, never from the outer DTO.
/// </summary>
public sealed class AgentVersionPayload
{
    /// <summary>Current agent version the server considers authoritative.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase hex SHA-256 of the MSI installer hosted at /api/v1/agent/msi.
    /// Null when no MSI is configured on the server.
    /// Agents MUST refuse to install when this is null (cannot verify integrity).
    /// </summary>
    public string? MsiSha256 { get; set; }
}
