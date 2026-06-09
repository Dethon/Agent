namespace Domain.DTOs.Voice;

public record AudioFormat
{
    public required int SampleRateHz { get; init; }
    public required int SampleWidthBytes { get; init; }
    public required int Channels { get; init; }

    public static AudioFormat WyomingStandard { get; } = new()
    {
        SampleRateHz = 16_000,
        SampleWidthBytes = 2,
        Channels = 1
    };
}