namespace Domain.DTOs.Voice;

public record ArmedTimer
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public required AnnounceTarget Target { get; init; }
    public required int DurationSeconds { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime FiresAtUtc { get; init; }
}