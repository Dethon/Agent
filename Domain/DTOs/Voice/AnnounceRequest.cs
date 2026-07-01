namespace Domain.DTOs.Voice;

public record AnnounceRequest
{
    public required AnnounceTarget Target { get; init; }
    public required string Text { get; init; }
    public string? Voice { get; init; }
    public AnnouncePriority Priority { get; init; } = AnnouncePriority.Normal;
    public InsistentOptions? Insistent { get; init; }
}