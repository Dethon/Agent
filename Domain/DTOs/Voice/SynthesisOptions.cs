namespace Domain.DTOs.Voice;

public record SynthesisOptions
{
    public string? Voice { get; init; }
    public string? Language { get; init; }
    public AudioFormat? Format { get; init; }
}