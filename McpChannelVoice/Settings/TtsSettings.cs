namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public WyomingTtsConfig Wyoming { get; init; } = new();
}

public record WyomingTtsConfig
{
    public string Host { get; init; } = "wyoming-piper";
    public int Port { get; init; } = 10200;
    public string? Voice { get; init; }
}