namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public OpenAiTtsConfig OpenAi { get; init; } = new();
    public StreamingTtsConfig Streaming { get; init; } = new();
}

public record StreamingTtsConfig
{
    // Speak each complete sentence run as the agent produces it instead of waiting for the whole
    // turn to finish. Disabling restores buffer-until-StreamComplete: the kill switch if streamed
    // prosody or the segment handshake ever misbehave in the field.
    public bool Enabled { get; init; } = true;

    // The first flush is the one the user is waiting through, so it goes out at the first boundary
    // past a deliberately low bar. Later flushes are covered by audio already playing, so they use a
    // higher one — each flush is its own TTS request, and larger runs mean fewer requests, fewer
    // inter-segment gaps, and better cross-sentence prosody.
    public int FirstSegmentMinChars { get; init; } = 40;
    public int MinChars { get; init; } = 140;

    // Start a segment's synthesis when it is queued rather than when the playback loop reaches it.
    // The loop is sequential and does not touch a job's audio until the previous one has finished
    // its real-time drain, so without this every sentence seam costs a full TTS round trip.
    public bool Prefetch { get; init; } = true;

    // How far ahead the prefetch may run before parking, in chunks. Bounded so a long utterance
    // cannot buffer its entire synthesis into memory.
    public const int DefaultPrefetchBufferChunks = 64;
    public int PrefetchBufferChunks { get; init; } = DefaultPrefetchBufferChunks;

    // Reply segments get their own queue allowance instead of sharing Announce.QueueMaxDepth, which
    // was sized when a reply was a single job. One turn's answer is a single logical unit: refusing
    // part of it leaves a hole in the middle of what the user hears, which is worse than a deep
    // queue. Sized far above any real answer (64 segments is ~9,000 characters of speech) so it
    // bounds a runaway rather than shaping normal replies.
    public const int DefaultMaxQueuedSegments = 64;
    public int MaxQueuedSegments { get; init; } = DefaultMaxQueuedSegments;
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