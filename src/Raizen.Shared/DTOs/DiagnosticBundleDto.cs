namespace Raizen.Shared.DTOs;

public sealed class DiagnosticBundleUploadDto
{
    public Guid RequestId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class DiagnosticBundleDto
{
    public Guid Id { get; set; }
    public Guid EndpointRegistrationId { get; set; }
    public Guid RequestId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int SizeBytes { get; set; }
    public int EventCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class DiagnosticBundleUploadResponseDto
{
    public Guid Id { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
