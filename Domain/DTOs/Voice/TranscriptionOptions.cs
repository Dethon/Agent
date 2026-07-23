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
}