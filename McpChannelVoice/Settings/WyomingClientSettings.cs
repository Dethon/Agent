namespace McpChannelVoice.Settings;

public record WyomingClientSettings
{
    // Delay before re-dialing a satellite after its connection drops.
    public int ReconnectDelaySeconds { get; init; } = 5;

    // End-of-utterance detection (see SilenceGate + AdaptiveLevelTracker). Tuned for
    // 16 kHz/16-bit mono. SilenceRmsThreshold is the quiet-room clamp: the adaptive
    // floor criterion only ever raises the effective bar above it, never lowers it.
    public double SilenceRmsThreshold { get; init; } = 500;
    public int TrailingSilenceMs { get; init; } = 800;
    public int MaxUtteranceMs { get; init; } = 15_000;
    public int MinSpeechMs { get; init; } = 200;
    public int FloorWindowMs { get; init; } = 3000;
    public double EnterMarginDb { get; init; } = 9;
    public double ExitMarginDb { get; init; } = 4;
    public double PeakDropDb { get; init; } = 15;

    // Capture-level accept bar: a capture ending on trailing silence is demoted to no-speech
    // unless its speech peak stands this far above the converged floor. Independent of
    // EnterMarginDb so it can be tuned without moving the per-frame speech classification;
    // null inherits EnterMarginDb. Field bracket (2026-07-20, fran-office/XVF3800): TV leaks
    // ran 14-19 dB over floor while a real AGC-compressed command sat at 11 dB — values
    // above ~11 dB trade TV rejection directly for dropped soft commands.
    public double? DemoteMarginDb { get; init; }
}