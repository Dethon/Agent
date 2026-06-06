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

    // Per-sample int16 amplitude below which tail audio is treated as silence and trimmed from each
    // synthesized utterance, tightening the gap before the follow-up beep. 0 disables trimming.
    public int TrailingSilenceTrimThreshold { get; init; } = 500;
}