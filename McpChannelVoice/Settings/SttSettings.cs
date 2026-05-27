namespace McpChannelVoice.Settings;

public record SttSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingSttConfig? Wyoming { get; init; }
    public OpenAiSttConfig? OpenAi { get; init; }
}

public record WyomingSttConfig
{
    public string Host { get; init; } = "wyoming-whisper";
    public int Port { get; init; } = 10300;
    public string? Model { get; init; }
    public string? Language { get; init; }
}

public record OpenAiSttConfig
{
    public string Model { get; init; } = "whisper-1";
}