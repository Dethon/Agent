namespace McpChannelVoice.Settings;

public record VoiceSettings
{
    // Agent that handles voice transcripts. Sent as the message AgentId so the agent
    // host resolves this definition; null falls back to the first configured agent.
    public string? AgentId { get; init; }
    public WyomingClientSettings WyomingClient { get; init; } = new();
    public SttSettings Stt { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
    public double ConfidenceThreshold { get; init; } = 0.4;
    public AnnounceSettings Announce { get; init; } = new();
    public Dictionary<string, SatelliteConfig> Satellites { get; init; } = new();
    public TimeSpan ConversationLifetime { get; init; } = TimeSpan.FromMinutes(5);
}