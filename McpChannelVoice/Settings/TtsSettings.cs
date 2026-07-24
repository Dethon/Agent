namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public OpenAiTtsConfig OpenAi { get; init; } = new();
}

public record OpenAiTtsConfig
{
    public string BaseUrl { get; init; } = "http://lemonade:13305/v1";
    public string Model { get; init; } = "kokoro-v1";

    // Kokoro voice id. es-419 Spanish voices: ef_dora (female), em_alex, em_santa.
    // Castilian quality is deliberately out of scope for this migration.
    public string? Voice { get; init; } = "em_santa";
    public double Speed { get; init; } = 1.2;

    // Per-sample int16 amplitude below which tail audio is treated as silence and trimmed from each
    // synthesized utterance, tightening the gap before the follow-up beep. 0 disables trimming.
    public int TrailingSilenceTrimThreshold { get; init; } = 500;
}