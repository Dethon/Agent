namespace McpChannelVoice.Settings;

public record SttSettings
{
    public OpenAiSttConfig OpenAi { get; init; } = new();
    public SegmentedSttConfig Streaming { get; init; } = new();
}

public record OpenAiSttConfig
{
    public string BaseUrl { get; init; } = "http://lemonade:13305/v1";

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

    // Bounds the transcription POST only — audio capture length is the speaker's business. The
    // shared Lemonade HttpClient has an infinite timeout for streaming TTS, so without this a
    // Lemonade that accepts connections but never answers stalls the utterance indefinitely.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;
}