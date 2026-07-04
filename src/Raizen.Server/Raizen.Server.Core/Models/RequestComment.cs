namespace Raizen.Server.Core.Models;

public sealed class RequestComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public ElevationRequest Request { get; set; } = null!;
    public string AuthorUpn { get; set; } = string.Empty;
    public string AuthorDisplayName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
