using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record WakeClaim(
    string SatelliteId, double? WakeRms, double? WakeScore, string Source, long ReceivedAt);

public sealed record ArbitrationCandidate(WakeClaim Claim, double? CalibratedRms);

// The pure decision core of multi-satellite wake arbitration: no clocks, no I/O, no state —
// timestamps come in as TimeProvider ticks with an explicit frequency so every rule is testable
// with plain numbers.
public static class WakeArbitrationRules
{
    // The Source a satellite reports for a physical button press (anything else is a wake word).
    // One const so the rule that ranks it and the rule that exempts it can never drift apart.
    public const string ButtonSource = "button";

    // Rule A: button (deliberate physical intent) beats any wake; then loudest calibrated mic;
    // missing rms (legacy firmware) ranks below every reported value; final tie -> first heard.
    public static ArbitrationCandidate PickWinner(IReadOnlyList<ArbitrationCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Claim.Source == ButtonSource ? 1 : 0)
            .ThenByDescending(c => c.CalibratedRms ?? double.NegativeInfinity)
            .ThenBy(c => c.Claim.ReceivedAt)
            .First();

    public static long MsToTicks(long ms, long frequency) => ms * frequency / 1000;

    // Where the wake word physically was, on the hub receive-time axis: detection fires a
    // measured ~DetectionLatencyMs after the word ends.
    public static (long Start, long End) WakeWordSpan(
        long receivedAt, long frequency, ArbitrationSettings settings)
    {
        var end = receivedAt - MsToTicks(settings.DetectionLatencyMs, frequency);
        return (end - MsToTicks(settings.WakeWordDurationMs, frequency), end);
    }

    // Rule B discriminator: did this open capture register a speech ONSET while the wake word
    // was being spoken? An onset is speech preceded by at least QuietGapMs of non-speech.
    // Speech running continuously since before the span is a DIFFERENT speaker talking to this
    // mic (not aligned); a capture opened inside the span has no earlier history to disprove an
    // onset, so in-span speech counts.
    public static bool HasAlignedOnset(
        CaptureActivity activity, long spanStart, long spanEnd, long frequency,
        ArbitrationSettings settings)
    {
        var slack = MsToTicks(settings.AlignSlackMs, frequency);
        var quietGap = MsToTicks(settings.QuietGapMs, frequency);
        var from = spanStart - slack;
        var to = spanEnd + slack;

        var firstSpeechInSpan = activity.Samples
            .Where(s => s.IsSpeech && s.Timestamp >= from && s.Timestamp <= to)
            .Select(s => (long?)s.Timestamp)
            .FirstOrDefault();
        if (firstSpeechInSpan is not { } onset)
        {
            return false;
        }
        if (activity.OpenedAt >= from)
        {
            return true;
        }
        return activity.Samples.All(s =>
            !s.IsSpeech || s.Timestamp >= onset || s.Timestamp < onset - quietGap);
    }

    public static double SpanPeakRms(CaptureActivity activity, long from, long to) =>
        activity.Samples
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .Select(s => s.Rms)
            .DefaultIfEmpty(0)
            .Max();

    public static double Calibrate(double rms, double offsetDb) => rms * Math.Pow(10, offsetDb / 20);

    public static bool CanSteal(
        double challengerCalibratedRms, double holderCalibratedPeak, double stealMarginDb) =>
        challengerCalibratedRms >= holderCalibratedPeak * Math.Pow(10, stealMarginDb / 20);
}