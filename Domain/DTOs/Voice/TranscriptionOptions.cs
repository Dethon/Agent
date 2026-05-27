namespace Domain.DTOs.Voice;

public record TranscriptionOptions
{
    public string? Language { get; init; }
    public string? ModelHint { get; init; }
    public TimeSpan? Timeout { get; init; }
}