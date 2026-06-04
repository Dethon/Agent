namespace McpChannelVoice.Settings;

public record SttSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingSttConfig? Wyoming { get; init; }
    public OpenAiSttConfig? OpenAi { get; init; }
    public OpenRouterSttConfig? OpenRouter { get; init; }
    public SegmentedSttConfig Streaming { get; init; } = new();
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

public record OpenRouterSttConfig
{
    public string Model { get; init; } = "openai/whisper-1";
    public string? Language { get; init; }
}

public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;
    public bool FinalReconcile { get; init; }
}