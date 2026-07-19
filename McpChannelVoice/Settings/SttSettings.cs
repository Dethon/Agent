namespace McpChannelVoice.Settings;

public record SttSettings
{
    public WyomingSttConfig Wyoming { get; init; } = new();
    public OpenAiSttConfig OpenAi { get; init; } = new();
    public SegmentedSttConfig Streaming { get; init; } = new();
}

public record OpenAiSttConfig
{
    public string BaseUrl { get; init; } = "http://mcp-lemonade:13305/v1";

    // Lemonade catalog name. The cpu and gpu tiers run the same whisper.cpp engine on the same
    // model (only the device flips), so STT_BACKEND never changes this — it is a container-side
    // concern. Override only to trade accuracy for speed (Whisper-Small) or the reverse
    // (Whisper-Large-v3 / Whisper-Large-v3-Turbo).
    public string Model { get; init; } = "Whisper-Medium";
    public string? Language { get; init; }

    // Gibberish gate: drop transcripts whose avg_logprob falls below the floor or whose
    // no_speech_prob rises above the ceiling. Null signals fail open (TranscriptDispatcher).
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;
}

public record WyomingSttConfig
{
    public string Host { get; init; } = "wyoming-whisper";
    public int Port { get; init; } = 10300;
    public string? Language { get; init; }
}

public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;
}