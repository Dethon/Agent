using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WakeArbitrationRulesTests
{
    private const long Freq = 1000; // 1 tick == 1 ms in these tests
    private static readonly ArbitrationSettings _settings = new();

    private static ArbitrationCandidate Candidate(
        string id, double? rms, string source = "wake", long receivedAt = 10_000) =>
        new(new WakeClaim(id, rms, null, source, receivedAt), rms);

    [Fact]
    public void PickWinner_LoudestCalibratedRmsWins()
    {
        var winner = WakeArbitrationRules.PickWinner(
            [Candidate("far", 200), Candidate("near", 900)]);
        winner.Claim.SatelliteId.ShouldBe("near");
    }

    [Fact]
    public void PickWinner_MissingRmsLosesToAnyReported()
    {
        var winner = WakeArbitrationRules.PickWinner(
            [Candidate("legacy", null, receivedAt: 1), Candidate("new", 50, receivedAt: 2)]);
        winner.Claim.SatelliteId.ShouldBe("new");
    }

    [Fact]
    public void PickWinner_AllMissingRms_EarliestArrivalWins()
    {
        var winner = WakeArbitrationRules.PickWinner(
            [Candidate("late", null, receivedAt: 300), Candidate("early", null, receivedAt: 100)]);
        winner.Claim.SatelliteId.ShouldBe("early");
    }

    [Fact]
    public void PickWinner_ButtonBeatsLouderWake()
    {
        var winner = WakeArbitrationRules.PickWinner(
            [Candidate("shouter", 5000), Candidate("presser", 10, source: "button")]);
        winner.Claim.SatelliteId.ShouldBe("presser");
    }

    [Fact]
    public void WakeWordSpan_RewindsDetectionLatencyAndWordDuration()
    {
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, _settings);
        end.ShouldBe(10_000 - 181);
        start.ShouldBe(10_000 - 181 - 700);
    }

    [Fact]
    public void Calibrate_AppliesDbOffset()
    {
        WakeArbitrationRules.Calibrate(100, 6).ShouldBe(100 * Math.Pow(10, 0.3), tolerance: 0.01);
        WakeArbitrationRules.Calibrate(100, 0).ShouldBe(100);
    }

    [Fact]
    public void CanSteal_RequiresTheMarginNotJustLouder()
    {
        // 6 dB margin ~= x1.995 in amplitude
        WakeArbitrationRules.CanSteal(199, 100, 6).ShouldBeFalse();
        WakeArbitrationRules.CanSteal(200, 100, 6).ShouldBeTrue();
    }

    // Rule B onset alignment. Span here: word start 9_119, word end 9_819 (from
    // WakeWordSpan(10_000)); slack 250, quiet gap 400.

    private static CaptureActivity Activity(long openedAt, params (long T, double Rms, bool Speech)[] samples) =>
        new(openedAt, samples.Select(s => new ChunkSample(s.T, s.Rms, s.Speech)).ToArray());

    [Fact]
    public void HasAlignedOnset_SpeechStartingInSpanAfterQuiet_IsAligned()
    {
        // quiet history, then speech right where the wake word was spoken
        var activity = Activity(5_000,
            (8_000, 40, false), (8_500, 42, false), (9_000, 45, false),
            (9_200, 800, true), (9_300, 900, true));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, _settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, _settings).ShouldBeTrue();
    }

    [Fact]
    public void HasAlignedOnset_MidSpeechSinceBeforeSpan_IsNotAligned()
    {
        // someone has been talking to this mic continuously since long before the span
        var activity = Activity(5_000,
            (8_600, 850, true), (8_700, 900, true), (8_800, 870, true), (8_900, 860, true),
            (9_000, 880, true), (9_200, 800, true), (9_300, 900, true));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, _settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, _settings).ShouldBeFalse();
    }

    [Fact]
    public void HasAlignedOnset_SilentAcrossSpan_IsNotAligned()
    {
        var activity = Activity(5_000,
            (9_000, 40, false), (9_200, 45, false), (9_500, 42, false));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, _settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, _settings).ShouldBeFalse();
    }

    [Fact]
    public void HasAlignedOnset_CaptureOpenedInSpanWithImmediateSpeech_IsAligned()
    {
        // follow-up window opened mid-span: no pre-span history exists to disprove an onset
        var activity = Activity(9_200, (9_250, 800, true), (9_330, 850, true));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, _settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, _settings).ShouldBeTrue();
    }

    [Fact]
    public void SpanPeakRms_MaxWithinRangeZeroWhenEmpty()
    {
        var activity = Activity(0, (100, 500, true), (200, 900, true), (900, 9_999, true));
        WakeArbitrationRules.SpanPeakRms(activity, 50, 250).ShouldBe(900);
        WakeArbitrationRules.SpanPeakRms(activity, 300, 800).ShouldBe(0);
    }
}