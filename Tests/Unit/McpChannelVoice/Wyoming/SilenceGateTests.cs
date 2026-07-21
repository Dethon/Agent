using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class SilenceGateTests
{
    // 16 kHz, 16-bit, mono => 2 bytes/sample => 3200 bytes == 100 ms.
    private const int Rate = 16_000;
    private const int Width = 2;
    private const int Channels = 1;
    private const int ChunkBytes = 3200;

    private static byte[] Loud()
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            // Int16 value 8000 (little-endian) => RMS well above the threshold.
            pcm[i] = 0x40;
            pcm[i + 1] = 0x1F;
        }
        return pcm;
    }

    private static byte[] Silent() => new byte[ChunkBytes];

    private static AdaptiveLevelTracker Tracker() => new(
        clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
        floorWindow: TimeSpan.FromSeconds(3));

    private static SilenceGate NewGate() => new(
        Tracker(),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(2000),
        minSpeech: TimeSpan.FromMilliseconds(100));

    private static SilenceGate.Decision Feed(SilenceGate gate, byte[] pcm) =>
        gate.Process(pcm, Rate, Width, Channels);

    [Fact]
    public void Process_TrailingSilenceAfterSpeech_EndsUtterance()
    {
        var gate = NewGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue); // pre-roll gap seeds the floor
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.EndUtterance);
    }

    [Fact]
    public void Process_SilenceBeforeSpeech_DoesNotEnd()
    {
        var gate = NewGate();

        foreach (var _ in Enumerable.Range(0, 5))
        {
            Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        }
    }

    [Fact]
    public void Process_BriefPauseBetweenSpeech_DoesNotEnd()
    {
        var gate = NewGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue); // pre-roll gap seeds the floor
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        // Only one silent chunk since the last speech => trailing silence not yet reached.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
    }

    [Fact]
    public void Process_ExceedsMaxUtterance_EndsEvenWhileSpeaking()
    {
        var gate = NewGate();

        // 2000 ms cap / 100 ms per chunk => the 20th chunk crosses the cap.
        var decisions = Enumerable.Range(0, 20).Select(_ => Feed(gate, Loud())).ToList();

        decisions.Take(19).ShouldAllBe(d => d == SilenceGate.Decision.Continue);
        decisions[^1].ShouldBe(SilenceGate.Decision.EndUtterance);
    }

    [Fact]
    public void Process_OnlyBlipOfSpeechThenSilence_WaitsForMaxUtterance()
    {
        var gate = NewGate();

        // A capture opening directly on a loud chunk seeds the floor at that level, so
        // the blip never counts as speech at all — trailing silence must not end early.
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
    }

    [Fact]
    public void SpeechElapsed_AccumulatesSpeechAndIgnoresSilence()
    {
        var gate = NewGate();

        Feed(gate, Silent()); // pre-roll gap seeds the floor
        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Silent()); // silence — must not count

        gate.SpeechElapsed.ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    private static SilenceGate FollowUpGate() => new(
        Tracker(),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(10_000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(500));

    [Fact]
    public void Process_NoSpeechWithinWindow_ReturnsNoSpeech()
    {
        var gate = FollowUpGate();

        // 500 ms window / 100 ms per chunk => the 5th silent chunk crosses it.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
    }

    [Fact]
    public void Process_SpeechBeforeWindowExpires_DoesNotReturnNoSpeech()
    {
        var gate = FollowUpGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);   // speech starts
        // Keep feeding past the no-speech window: speech started, so NoSpeech must never fire.
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Loud()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }

    [Fact]
    public void Process_SubMinSpeechBlipThenSilence_StillTimesOutAsNoSpeech()
    {
        var gate = FollowUpGate();

        // A single 100 ms loud chunk does NOT exceed the 100 ms minSpeech gate, so it is noise.
        // A noise blip (echo tail, a cough) must NOT disable the no-speech window — otherwise the
        // capture hangs open until the maxUtterance cap. The window must still expire as NoSpeech.
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
    }

    [Fact]
    public void Process_NoSpeechTimeoutDisabledByDefault_NeverReturnsNoSpeech()
    {
        var gate = NewGate(); // default gate has noSpeechTimeout = default (disabled)

        foreach (var _ in Enumerable.Range(0, 30))
        {
            Feed(gate, Silent()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }

    [Fact]
    public void PeakRms_TracksLoudestChunkSeen()
    {
        var gate = NewGate();

        Feed(gate, Silent());
        Feed(gate, Loud());
        Feed(gate, Silent());

        gate.PeakRms.ShouldBe(8000, 1.0);
    }

    [Fact]
    public void PeakRms_Reset_ClearsIt()
    {
        var gate = NewGate();
        Feed(gate, Loud());

        gate.Reset();

        gate.PeakRms.ShouldBe(0);
    }

    private static byte[] Tone(short amplitude)
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return pcm;
    }

    // Short 400 ms floor window so adaptivity engages within a few chunks.
    private static SilenceGate BabbleGate(int noSpeechMs = 0, int? floorSmoothingMs = null) => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromMilliseconds(400),
            floorSmoothing: floorSmoothingMs is null ? null : TimeSpan.FromMilliseconds(floorSmoothingMs.Value)),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(60_000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(noSpeechMs));

    [Fact]
    public void Process_SpeechOverBabble_EndsOnReturnToBabble()
    {
        var gate = BabbleGate();

        // TV-like babble (RMS 2000, above the 500 clamp). THE bug this change fixes:
        // with the fixed threshold this stream never ends before the cap. Adaptively,
        // babble is silence from chunk one — it must never end the turn on its own.
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        }

        Feed(gate, Tone(8000)).ShouldBe(SilenceGate.Decision.Continue); // user speaks
        Feed(gate, Tone(8000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue); // back to babble
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.EndUtterance);
        gate.EndReason.ShouldBe("trailing_silence");
    }

    [Fact]
    public void Process_BabbleOnlyFollowUp_TimesOutAsNoSpeech()
    {
        var gate = BabbleGate(noSpeechMs: 500);

        // TV alone in a follow-up window: never speech, so the no-speech
        // window must expire instead of dispatching TV dialog to the agent.
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.NoSpeech);
        gate.EndReason.ShouldBe("no_speech");
    }

    [Fact]
    public void Process_TvResumesAfterLullSeededFloor_KeepsCapturing()
    {
        var gate = BabbleGate(noSpeechMs: 500);

        // KNOWN LIMITATION, deliberately pinned (2026-07-21). A capture opening during a
        // TV lull seeds the floor at near-silence, so resumed TV dialog latches as speech.
        // The gate used to recover by letting the floor converge up until that pseudo-
        // speech no longer stood above it, then demoting the capture to no-speech. That
        // convergence is exactly what truncated real long messages (the floor cannot tell
        // a talking person from a talking television), so the floor now freezes at the
        // first accepted speech frame and this capture runs on instead of being demoted.
        // Rejecting it is speaker verification's job now — it scores TV at 0.38-0.42
        // against 0.50-0.85 for an enrolled voice, which is a discriminator single-mic
        // energy simply does not have. If you are here because you want the demote back,
        // read the freeze rationale in AdaptiveLevelTracker.IsSpeech first.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);  // lull seeds the floor
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        foreach (var _ in Enumerable.Range(0, 6))
        {
            Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue); // TV rides on
        }

        gate.EndReason.ShouldBeNull();
    }

    [Fact]
    public void Process_UserSpeechOverBabbleWithNoSpeechWindow_StillEndsUtterance()
    {
        // Explicit near-zero smoothing: at this test's compressed scale the trailing run
        // (200 ms) is shorter than the default smoothing window (500 ms), so the end-time
        // floor would still carry the user's own speech energy — a state production cannot
        // reach (TrailingSilenceMs 2000 >= smoothing 500 guarantees pure-background floor
        // entries at end time). Near-zero smoothing restores prod-shaped arithmetic.
        var gate = BabbleGate(noSpeechMs: 5000, floorSmoothingMs: 100);

        // Regression guard for the lull-seed fix: real near-field speech stands well
        // above the converged floor, so the end-of-capture prominence check must let
        // it through — same scenario as Process_SpeechOverBabble_EndsOnReturnToBabble
        // but with the no-speech window armed, as production always is.
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue);
        }
        Feed(gate, Tone(8000)).ShouldBe(SilenceGate.Decision.Continue); // user speaks
        Feed(gate, Tone(8000)).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.Continue); // back to babble
        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.EndUtterance);
        gate.EndReason.ShouldBe("trailing_silence");
    }

    [Fact]
    public void Process_MaxUtteranceCap_ReportsEndReason()
    {
        var gate = NewGate();

        foreach (var _ in Enumerable.Range(0, 19))
        {
            Feed(gate, Loud());
        }
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.EndUtterance);
        gate.EndReason.ShouldBe("max_utterance");
    }

    [Fact]
    public void Process_NoSpeechTimeout_ReportsEndReason()
    {
        var gate = FollowUpGate();

        foreach (var _ in Enumerable.Range(0, 4))
        {
            Feed(gate, Silent());
        }
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
        gate.EndReason.ShouldBe("no_speech");
    }

    [Fact]
    public void EndReason_BeforeAnyTerminalDecision_IsNull()
    {
        var gate = NewGate();

        Feed(gate, Silent());
        Feed(gate, Loud());

        gate.EndReason.ShouldBeNull();
    }

    [Fact]
    public void TrailingRms_ExposesMeanLevelOfTheTrailingRun()
    {
        var gate = BabbleGate();
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Tone(2000));
        }
        Feed(gate, Tone(8000)); // user speaks
        Feed(gate, Tone(8000));

        Feed(gate, Tone(2000)); // back to babble: trailing run
        gate.TrailingRms.ShouldBe(2000, 1.0);

        Feed(gate, Tone(2000)).ShouldBe(SilenceGate.Decision.EndUtterance);
        gate.TrailingRms.ShouldBe(2000, 1.0); // still readable once the capture ends (stats path)
    }

    [Fact]
    public void TrailingRms_SpeechResumingResetsTheRun()
    {
        var gate = BabbleGate();
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Tone(2000));
        }
        Feed(gate, Tone(8000));  // speech
        Feed(gate, Tone(2000));  // trailing babble
        Feed(gate, Tone(24000)); // speech resumes above any bar: run resets

        gate.TrailingRms.ShouldBe(0);
    }

    [Fact]
    public void FloorRms_ExposesTrackerEstimate()
    {
        var gate = BabbleGate();

        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Tone(2000));
        }

        gate.FloorRms.ShouldBe(2000, 50);
    }

    [Fact]
    public void Reset_ClearsEndReason()
    {
        var gate = NewGate();
        foreach (var _ in Enumerable.Range(0, 20))
        {
            Feed(gate, Loud());
        }
        gate.EndReason.ShouldBe("max_utterance");

        gate.Reset();

        gate.EndReason.ShouldBeNull();
    }

    [Fact]
    public void LastFrameWasSpeech_TracksPerChunkClassification()
    {
        var gate = NewGate();

        Feed(gate, Silent()); // pre-roll gap seeds the floor
        gate.LastFrameWasSpeech.ShouldBeFalse();
        Feed(gate, Loud());
        gate.LastFrameWasSpeech.ShouldBeTrue();
        Feed(gate, Silent());
        gate.LastFrameWasSpeech.ShouldBeFalse();
    }

    // Exact production wiring (appsettings.json WyomingClient + FollowUp.WindowMs as the
    // no-speech window), so this pins the real deployed behaviour rather than a test rig.
    private static SilenceGate ProductionGate() => new(
        new AdaptiveLevelTracker(
            clampRms: 700, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 10,
            floorWindow: TimeSpan.FromSeconds(3), demoteMarginDb: 9),
        trailingSilence: TimeSpan.FromSeconds(2),
        maxUtterance: TimeSpan.FromSeconds(40),
        minSpeech: TimeSpan.FromMilliseconds(300),
        noSpeechTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public void Process_ContinuousSpeechPastFloorWindow_DoesNotEndUtterance()
    {
        // Field report 2026-07-21: "I keep talking but at some point the hub stops
        // listening as if I had finished talking." Measured on real speech fixtures,
        // captures died 9-13 s in — t_cut = lead-in + FloorWindowMs + TrailingSilenceMs.
        // Someone still speaking must never be endpointed, however long they go on.
        var gate = ProductionGate();
        foreach (var _ in Enumerable.Range(0, 5)) // 500 ms of quiet room before speaking
        {
            Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        }

        foreach (var _ in Enumerable.Range(0, 100)) // 10 s of unbroken speech
        {
            Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        }
    }
}