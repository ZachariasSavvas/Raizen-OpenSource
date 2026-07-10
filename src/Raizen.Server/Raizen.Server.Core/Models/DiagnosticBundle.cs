namespace Raizen.Server.Core.Models;

public sealed class DiagnosticBundle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EndpointRegistrationId { get; set; }
    public EndpointRegistration Endpoint { get; set; } = null!;
    public Guid RequestId { get; set; }
    public ElevationRequest Request { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/zip";
    public byte[] Content { get; set; } = [];
    public string Sha256 { get; set; } = string.Empty;
    public int SizeBytes { get; set; }
    public int EventCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
}
