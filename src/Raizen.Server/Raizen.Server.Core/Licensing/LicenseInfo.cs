namespace Raizen.Server.Core.Licensing;

public sealed record LicenseInfo
{
    public string LicensedTo { get; init; } = string.Empty;
    public string LicenseId { get; init; } = string.Empty;
    public int MaxEndpoints { get; init; }
    public string IssuedAt { get; init; } = string.Empty;

    /// <summary>ISO 8601 date string, or null for perpetual licenses.</summary>
    public string? ExpiresAt { get; init; }

    public string Tier { get; init; } = string.Empty;
    /// <summary>SHA-256 hardware fingerprints (CPU ID + Disk Serial + MAC + Machine GUID).</summary>
    public List<string> AllowedFingerprints { get; init; } = [];
    public string? Notes { get; init; }

    public bool IsExpired =>
        ExpiresAt is not null &&
        DateOnly.TryParse(ExpiresAt, out var d) &&
        d < DateOnly.FromDateTime(DateTime.UtcNow);

    public bool IsPerpetual => ExpiresAt is null;
}
