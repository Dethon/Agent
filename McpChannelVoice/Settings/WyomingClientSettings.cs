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
    // Adaptive-regime backstop: a frame more than PeakDropDb below the utterance peak is
    // background, not speech. Field-tuned to 10 dB (was 15): with a loud TV the XVF3800 AGC
    // compresses near-field speech to only ~13-16 dB over the TV, so a 15 dB drop let TV ride in
    // as speech and buried the command under 15-40 s of audio; 10 dB ends the capture ~2 s after
    // the speaker stops while still dropping pure TV. Only armed in the adaptive regime (loud
    // room), so it never clips speech in a quiet room — which is only true because the floor
    // freezes at the first accepted speech frame (AdaptiveLevelTracker.IsSpeech). Before that
    // freeze, sustained speech drove the floor into the adaptive regime in a silent room and
    // armed this backstop against the speaker.
    public double PeakDropDb { get; init; } = 10;

    // Capture-level accept bar: a capture ending on trailing silence is demoted to no-speech
    // unless its speech peak stands this far above the floor. Independent of EnterMarginDb so
    // it can be tuned without moving the per-frame speech classification; null inherits
    // EnterMarginDb. Field bracket (2026-07-20, fran-office/XVF3800): TV leaks ran 14-19 dB
    // over floor while a real AGC-compressed command sat at 11 dB — values above ~11 dB trade
    // TV rejection directly for dropped soft commands. Note the floor is now frozen at first
    // speech, so at or below EnterMarginDb this bar is inert — see SpeechProminent.
    public double? DemoteMarginDb { get; init; }
}