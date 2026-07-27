namespace McpChannelVoice.Settings;

// Multi-satellite wake arbitration: several satellites hearing one utterance are resolved to a
// single winner by calibrated wake-word loudness. All timing knobs are hub receive-time; the
// wake-word span is reconstructed as
// [T_rx - DetectionLatencyMs - WakeWordDurationMs, T_rx - DetectionLatencyMs].
public record ArbitrationSettings
{
    public bool Enabled { get; init; } = true;
    public int WindowMs { get; init; } = 500;
    public double StealMarginDb { get; init; } = 6;
    public int DetectionLatencyMs { get; init; } = 181;
    public int WakeWordDurationMs { get; init; } = 700;
    public int AlignSlackMs { get; init; } = 250;
    public int QuietGapMs { get; init; } = 400;

    // How much per-chunk capture history Rule B needs: the whole reconstructed span plus
    // slack and the quiet-gap lookback, with a second of margin for scheduling jitter.
    public TimeSpan HistorySpan => TimeSpan.FromMilliseconds(
        DetectionLatencyMs + WakeWordDurationMs + AlignSlackMs + QuietGapMs + 1000);
}