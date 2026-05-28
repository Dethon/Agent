namespace McpChannelVoice.Settings;

public record SatelliteConfig
{
    public required string Identity { get; init; }
    public required string Room { get; init; }

    // Wyoming server URI the satellite listens on (e.g. tcp://host.docker.internal:10800).
    // The hub connects out to this address as a Wyoming client. Satellites without an
    // address are catalog-only (announce targets) and are never dialed.
    public string? Address { get; init; }

    public string? WakeWord { get; init; }
    public SttSettings? Stt { get; init; }
    public TtsSettings? Tts { get; init; }
}