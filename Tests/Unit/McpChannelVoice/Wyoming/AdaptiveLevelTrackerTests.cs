using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class AdaptiveLevelTrackerTests
{
    private const double ChunkMs = 100;

    private static AdaptiveLevelTracker Tracker(int floorWindowMs = 400) => new(
        clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
        floorWindow: TimeSpan.FromMilliseconds(floorWindowMs));

    private static bool Feed(AdaptiveLevelTracker tracker, double rms) =>
        tracker.IsSpeech(rms, ChunkMs);

    private static void FeedAll(AdaptiveLevelTracker tracker, double rms, int chunks)
    {
        foreach (var _ in Enumerable.Range(0, chunks))
        {
            Feed(tracker, rms);
        }
    }

    [Fact]
    public void IsSpeech_QuietRoom_BehavesLikeAbsoluteThreshold()
    {
        var tracker = Tracker();

        Feed(tracker, 0).ShouldBeFalse();
        Feed(tracker, 400).ShouldBeFalse();  // below clamp
        Feed(tracker, 8000).ShouldBeTrue();  // above clamp
        Feed(tracker, 400).ShouldBeFalse();
    }

    [Fact]
    public void IsSpeech_BabbleBackground_IsSilenceFromTheFirstChunk()
    {
        var tracker = Tracker();

        // The floor seeds at the babble level immediately, so TV-like background
        // never reads as speech — this is what stops minSpeech latching before
        // the user has said anything.
        Feed(tracker, 2000).ShouldBeFalse();
        Feed(tracker, 2000).ShouldBeFalse();
        Feed(tracker, 8000).ShouldBeTrue();   // user: 12 dB above the floor
        Feed(tracker, 8000).ShouldBeTrue();
        Feed(tracker, 2000).ShouldBeFalse();  // back at the floor => silence
    }

    [Fact]
    public void FloorDb_LevelDrops_FallsImmediately()
    {
        var tracker = Tracker();
        FeedAll(tracker, 2000, 6);

        Feed(tracker, 0); // duck engages / TV muted

        tracker.FloorDb.ShouldBe(0, 0.01);
        tracker.FloorRms.ShouldBe(1, 0.01);
    }

    [Fact]
    public void FloorDb_LevelSteps_ConvergesAfterWindowTurnover()
    {
        var tracker = Tracker();
        FeedAll(tracker, 0, 6);      // quiet baseline
        FeedAll(tracker, 2000, 3);   // duck restore: window still holds quiet frames

        tracker.FloorDb.ShouldBe(0, 0.01);      // not converged yet

        FeedAll(tracker, 2000, 3);   // quiet frames aged out

        tracker.FloorDb.ShouldBe(66.02, 0.1);   // floor is now the louder background
    }

    [Fact]
    public void IsSpeech_SlowGainRampOverBabbleFloor_StaysSilence()
    {
        var tracker = Tracker();
        FeedAll(tracker, 2000, 6);

        // ~1 dB per chunk upward drift (AGC lift / gradual TV volume creep):
        // the trailing window minimum follows, so the +9 dB entry bar is never crossed.
        foreach (var rms in new[] { 2200.0, 2400, 2650, 2900, 3200 })
        {
            Feed(tracker, rms).ShouldBeFalse();
        }
    }

    [Fact]
    public void IsSpeech_SameLevelDifferentState_HysteresisHolds()
    {
        var tracker = Tracker();
        FeedAll(tracker, 2000, 6); // floor 66 dB; enter bar 75 dB, exit bar 70 dB

        Feed(tracker, 3750).ShouldBeFalse();  // 71.5 dB: above exit bar but below entry bar
        Feed(tracker, 8000).ShouldBeTrue();   // speech enters at 78.1 dB
        Feed(tracker, 3750).ShouldBeTrue();   // same 71.5 dB now sustains active speech
    }

    [Fact]
    public void IsSpeech_FarBelowUtterancePeak_InAdaptiveRegime_ForcedSilence()
    {
        var tracker = Tracker();
        FeedAll(tracker, 2000, 6);   // adaptive regime armed (floor + 9 dB > clamp)
        FeedAll(tracker, 24000, 2);  // loud near-field speech: peak 87.6 dB

        // 71 dB: above the 70 dB exit bar (would sustain speech) but 16.6 dB below
        // the peak => background, ends the turn even though the floor never moved.
        Feed(tracker, 3550).ShouldBeFalse();
    }

    [Fact]
    public void IsSpeech_FarBelowPeakInQuietRoom_BackstopDisarmed()
    {
        var tracker = Tracker();
        FeedAll(tracker, 0, 6);      // quiet room: clamp regime
        Feed(tracker, 24000);        // shout: peak 87.6 dB

        // 20+ dB below peak but above the clamp: still speech — the backstop must
        // never clip quiet-room loud-then-soft deliveries (spec Goal 3).
        Feed(tracker, 2000).ShouldBeTrue();
    }

    [Fact]
    public void IsSpeech_CaptureOpensMidSpeech_RecoversAtFirstGap()
    {
        var tracker = Tracker(floorWindowMs: 3000);

        // Zero leading gap: the first loud chunks seed the floor at speech level
        // and read as silence. The first inter-word dip re-seeds the min and
        // subsequent speech classifies correctly.
        Feed(tracker, 8000).ShouldBeFalse();
        Feed(tracker, 8000).ShouldBeFalse();
        Feed(tracker, 0).ShouldBeFalse();
        Feed(tracker, 8000).ShouldBeTrue();
    }

    [Fact]
    public void IsSpeech_LoudTransientBeforeSpeech_DoesNotPoisonPeakBackstop()
    {
        var tracker = Tracker();

        // Capture opens ON a near-clipping transient: it seeds the floor at its own
        // level (90.1 dB), classifies as silence — and must NOT become the "utterance
        // peak". 32000 -> 90.1 dB; bar = floor + 9 => not speech.
        Feed(tracker, 32000).ShouldBeFalse();
        FeedAll(tracker, 1500, 6);           // transient ages out; floor 63.52 dB

        // 5000 -> 73.98 dB: above the 72.52 dB entry bar, but 16.1 dB below the
        // transient — with a poisoned peak the backstop would force silence here.
        Feed(tracker, 5000).ShouldBeTrue();
    }

    [Fact]
    public void FloorDb_ZeroDurationFrame_DoesNotEnterTheWindow()
    {
        var tracker = Tracker();
        FeedAll(tracker, 2000, 6); // floor 66.02 dB

        tracker.IsSpeech(0, 0);    // malformed frame: zero rms, zero duration

        tracker.FloorDb.ShouldBe(66.02, 0.1); // floor must not be slammed to 0 dB
    }
}