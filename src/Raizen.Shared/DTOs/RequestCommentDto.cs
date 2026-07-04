namespace Raizen.Shared.DTOs;

public sealed class RequestCommentDto
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string AuthorUpn { get; set; } = string.Empty;
    public string AuthorDisplayName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AddCommentDto
{
    public string Body { get; set; } = string.Empty;
}
