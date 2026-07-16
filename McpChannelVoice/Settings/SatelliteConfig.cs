namespace McpChannelVoice.Settings;

public record SatelliteConfig
{
    public required string Identity { get; init; }
    public required string Room { get; init; }

    // Optional geographic locality (e.g. "Madrid, Spain") for the room. Unlike Room — which is a
    // routing key (announcements, metrics) — this is purely surfaced to the LLM via DisplayLocation
    // so it can answer location-aware questions (weather, local activities). When null it inherits
    // the channel-wide default (VoiceSettings.Locality) at settings load.
    public string? Locality { get; init; }

    // The location string shown to the agent: the room, enriched with the locality when present.
    public string DisplayLocation =>
        string.IsNullOrWhiteSpace(Locality) ? Room : $"{Room} ({Locality})";

    // Wyoming server URI the satellite listens on (e.g. tcp://host.docker.internal:10800).
    // The hub connects out to this address as a Wyoming client. Satellites without an
    // address are catalog-only (announce targets) and are never dialed.
    public string? Address { get; init; }

    public string? WakeWord { get; init; }

    // Per-satellite override of FollowUpSettings.Enabled. Null inherits the global value.
    public bool? FollowUpEnabled { get; init; }
    public SttSettings? Stt { get; init; }
    public TtsSettings? Tts { get; init; }

    // Per-satellite overrides of the outer SilenceGate entry bar (WyomingClientSettings).
    // Mic front-ends sit at different noise floors (e.g. XVF3800 firmware AGC lifts quiet
    // rooms toward speech levels), so the global values can't fit every unit.
    public GateSettings? Gate { get; init; }

    public double ResolveRmsThreshold(WyomingClientSettings global) =>
        Gate?.SilenceRmsThreshold ?? global.SilenceRmsThreshold;

    public int ResolveMinSpeechMs(WyomingClientSettings global) =>
        Gate?.MinSpeechMs ?? global.MinSpeechMs;
}

public record GateSettings
{
    public double? SilenceRmsThreshold { get; init; }
    public int? MinSpeechMs { get; init; }
}