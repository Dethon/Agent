namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingTtsConfig? Wyoming { get; init; }
    public OpenAiTtsConfig? OpenAi { get; init; }
    public OpenRouterTtsConfig? OpenRouter { get; init; }
}

public record WyomingTtsConfig
{
    public string Host { get; init; } = "wyoming-piper";
    public int Port { get; init; } = 10200;
    public string? Voice { get; init; }
}

public record OpenAiTtsConfig
{
    public string Model { get; init; } = "tts-1";
    public string Voice { get; init; } = "alloy";
}

public record OpenRouterTtsConfig
{
    public string Model { get; init; } = "openai/gpt-4o-mini-tts";
    public string Voice { get; init; } = "alloy";
}