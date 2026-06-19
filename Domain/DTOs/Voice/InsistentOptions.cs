namespace Domain.DTOs.Voice;

public record InsistentOptions
{
    public int? GapSeconds { get; init; }
    public int? MaxRepeats { get; init; }
    public int? MaxDurationSeconds { get; init; }
}