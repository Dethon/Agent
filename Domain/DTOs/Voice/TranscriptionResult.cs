namespace Domain.DTOs.Voice;

public record TranscriptionResult
{
    public required string Text { get; init; }
    public string? Language { get; init; }
    public double? Confidence { get; init; }
}