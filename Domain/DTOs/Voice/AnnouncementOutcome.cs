namespace Domain.DTOs.Voice;

public record AnnouncementOutcome
{
    public required string Id { get; init; }
    public required string Status { get; init; }
}