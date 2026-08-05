namespace Domain.DTOs.Voice;

public record TranscriptionOptions
{
    public string? Language { get; init; }
    public string? ModelHint { get; init; }
    public TimeSpan? Timeout { get; init; }
    // Target-speaker-extraction hints, set by the voice host from the speaker gate's verdict and
    // the capture's frozen pre-speech floor; consumed only by TseSpeechToText. Null TargetSpeaker
    // means extraction cannot run for this call.
    public string? TargetSpeaker { get; init; }
    public double? NoiseFloorRms { get; init; }
    // Originating satellite, so decorators deeper in the STT chain (TSE metrics/audit) can
    // attribute their events to the satellite/room the way the host's own publishes do.
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    // The satellite's configured identity, which is what Identity means on every voice event. It
    // travels with the id and the room so a decorator naming the satellite names all three, and so
    // the target speaker above never has to stand in for it.
    public string? Identity { get; init; }
    public string? Locality { get; init; }

    // Per-satellite override of the configured whisper biasing prompt; null falls back to the
    // global Stt.OpenAi.Prompt inside the backend, symmetric with Language above.
    public string? PromptTemplate { get; init; }

    // Text that immediately precedes this audio — the prior segment's transcript when a
    // segmenting decorator split the utterance. Composed into whisper's initial prompt so a
    // fragment is decoded as the continuation it actually is.
    public string? PriorText { get; init; }
}