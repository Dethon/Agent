namespace McpChannelVoice.Settings;

public record VoiceSettings
{
    public WyomingClientSettings WyomingClient { get; init; } = new();
    public SttSettings Stt { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
    public double ConfidenceThreshold { get; init; } = 0.4;
    public AnnounceSettings Announce { get; init; } = new();
    public Dictionary<string, SatelliteConfig> Satellites { get; init; } = new();
}