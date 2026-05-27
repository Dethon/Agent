namespace Domain.DTOs.Voice;

public record AnnounceResponse
{
    public required string AnnouncementId { get; init; }
    public required IReadOnlyList<AnnouncementOutcome> Satellites { get; init; }
}