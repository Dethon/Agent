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

    // Dedicated override records rather than the full SttSettings/TtsSettings: every field they
    // expose is honored per-satellite, so config can't advertise knobs that silently do nothing.
    // Env paths stay Satellites__<id>__Stt__OpenAi__* / Satellites__<id>__Tts__OpenAi__*.
    public SttOverrides? Stt { get; init; }
    public TtsOverrides? Tts { get; init; }

    // Per-satellite overrides of the outer SilenceGate entry bar (WyomingClientSettings).
    // Mic front-ends sit at different noise floors (e.g. XVF3800 firmware AGC lifts quiet
    // rooms toward speech levels), so the global values can't fit every unit.
    public GateSettings? Gate { get; init; }

    public double ResolveRmsThreshold(WyomingClientSettings global) =>
        Gate?.SilenceRmsThreshold ?? global.SilenceRmsThreshold;

    public int ResolveMinSpeechMs(WyomingClientSettings global) =>
        Gate?.MinSpeechMs ?? global.MinSpeechMs;

    // Mic front-ends and rooms sit at different noise floors, so the gibberish-gate thresholds
    // are per-satellite tunable like the SilenceGate entry bar above.
    public double ResolveAvgLogProbThreshold(double global) =>
        Stt?.OpenAi?.AvgLogProbThreshold ?? global;

    public double ResolveNoSpeechProbThreshold(double global) =>
        Stt?.OpenAi?.NoSpeechProbThreshold ?? global;

    public int ResolveFloorWindowMs(WyomingClientSettings global) =>
        Gate?.FloorWindowMs ?? global.FloorWindowMs;

    public double ResolveEnterMarginDb(WyomingClientSettings global) =>
        Gate?.EnterMarginDb ?? global.EnterMarginDb;

    public double ResolveExitMarginDb(WyomingClientSettings global) =>
        Gate?.ExitMarginDb ?? global.ExitMarginDb;

    public double ResolvePeakDropDb(WyomingClientSettings global) =>
        Gate?.PeakDropDb ?? global.PeakDropDb;

    public double? ResolveDemoteMarginDb(WyomingClientSettings global) =>
        Gate?.DemoteMarginDb ?? global.DemoteMarginDb;

    // Per-satellite overrides of the speaker-identity gate. Null inherits the global value.
    public VerificationOverrides? Verification { get; init; }

    public bool ResolveVerificationEnabled(SpeakerVerificationSettings global) =>
        Verification?.Enabled ?? global.Enabled;

    public double ResolveSimilarityThreshold(SpeakerVerificationSettings global) =>
        Verification?.SimilarityThreshold ?? global.SimilarityThreshold;

    public double ResolveShortSpeechSimilarityThreshold(SpeakerVerificationSettings global) =>
        Verification?.ShortSpeechSimilarityThreshold ?? global.ShortSpeechSimilarityThreshold;

    public int ResolveFullThresholdSpeechMs(SpeakerVerificationSettings global) =>
        Verification?.FullThresholdSpeechMs ?? global.FullThresholdSpeechMs;

    public double ResolveIdentifyThreshold(SpeakerVerificationSettings global) =>
        Verification?.IdentifyThreshold ?? global.IdentifyThreshold;

    public double ResolveShortSpeechIdentifyThreshold(SpeakerVerificationSettings global) =>
        Verification?.ShortSpeechIdentifyThreshold ?? global.ShortSpeechIdentifyThreshold;

    public double ResolveIdentifyMargin(SpeakerVerificationSettings global) =>
        Verification?.IdentifyMargin ?? global.IdentifyMargin;
}

public record GateSettings
{
    public double? SilenceRmsThreshold { get; init; }
    public int? MinSpeechMs { get; init; }
    public int? FloorWindowMs { get; init; }
    public double? EnterMarginDb { get; init; }
    public double? ExitMarginDb { get; init; }
    public double? PeakDropDb { get; init; }
    public double? DemoteMarginDb { get; init; }
}

public record SttOverrides
{
    public OpenAiSttOverrides? OpenAi { get; init; }
}

public record OpenAiSttOverrides
{
    public string? Language { get; init; }
    public double? AvgLogProbThreshold { get; init; }
    public double? NoSpeechProbThreshold { get; init; }
}

public record TtsOverrides
{
    public OpenAiTtsOverrides? OpenAi { get; init; }
}

public record OpenAiTtsOverrides
{
    public string? Voice { get; init; }
}

public record VerificationOverrides
{
    public bool? Enabled { get; init; }
    public double? SimilarityThreshold { get; init; }
    public double? ShortSpeechSimilarityThreshold { get; init; }
    public int? FullThresholdSpeechMs { get; init; }
    public double? IdentifyThreshold { get; init; }
    public double? ShortSpeechIdentifyThreshold { get; init; }
    public double? IdentifyMargin { get; init; }
}