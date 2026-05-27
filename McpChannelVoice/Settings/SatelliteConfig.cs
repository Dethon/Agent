namespace McpChannelVoice.Settings;

public record SatelliteConfig
{
    public required string Identity { get; init; }
    public required string Room { get; init; }
    public string? WakeWord { get; init; }
    public SttSettings? Stt { get; init; }
    public TtsSettings? Tts { get; init; }
}