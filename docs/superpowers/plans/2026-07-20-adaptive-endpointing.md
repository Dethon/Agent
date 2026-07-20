# Adaptive End-of-Utterance Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `SilenceGate`'s fixed absolute RMS threshold with an adaptive dual-reference (noise floor + utterance peak) speech classifier so captures end promptly with a TV playing in the room.

**Architecture:** A new pure class `AdaptiveLevelTracker` tracks a windowed-minimum noise floor and the utterance peak in dB; `SilenceGate` delegates its per-chunk speech/silence decision to it and keeps its whole state machine. The existing absolute threshold becomes a quiet-room clamp so no-background behavior is preserved. The same tracker serves the outer endpointer and `SegmentedSpeechToText`'s phrase slicer. `CaptureStats`/`VoiceEvent` grow `FloorRms`/`EndReason` for dashboard tuning.

**Tech Stack:** .NET 10, xUnit + Shouldly, no new dependencies.

**Spec:** `docs/superpowers/specs/2026-07-20-adaptive-endpointing-design.md` (approved). Refinements discovered during planning — Task 6 amends the spec with them:

1. **Smoothing:** the floor is a *pure* windowed minimum (no EMA). The min already falls instantly and rises only as quiet frames age out of the window — an EMA on top adds a constant without adding behavior.
2. **Floor seeds from the first real chunks — no "grace period".** With a TV in the room the floor locks onto the TV level from chunk one, so TV reads as *silence* immediately and can never latch `minSpeech` before the user speaks (a clamp-only grace window was considered and rejected: it made TV count as speech, latch `minSpeech`, and end the turn via trailing-silence *before the command* — losing it). Consequences: (a) a capture that opens *mid-loud-speech* with zero leading gap classifies that speech as floor until the first inter-word dip re-seeds the min — in practice the wake pre-roll (detection-latency gap, not the wake word) supplies gap frames, and dips arrive within a word or two; (b) on a TV-background wake turn the user must begin speaking within the existing no-speech window (`FollowUp.WindowMs`, 5 s) — same UX as speaking to the assistant today; (c) a TV-only follow-up window correctly times out as `no_speech` — the spec's original claim holds.
3. **Peak backstop arming:** the peak-drop rule applies only in the adaptive regime (`floor + EnterMarginDb > clamp`). In a quiet room it is disarmed, so loud-then-soft speech can never be clipped — spec Goal 3 (zero quiet-room regression) outranks backstop coverage there.

## Global Constraints

- Branch: `noise`. **Never switch branches.** Commit after each task with the `Claude-Session` trailer used by earlier commits on this branch.
- TDD non-negotiable: every task runs its RED step and **shows the failure output** before implementing.
- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`); the pre-commit hook re-formats and re-stages whole files — make the working tree match the intended commit.
- Codebase style: file-scoped namespaces, primary constructors, records for settings/DTOs, LINQ over loops, no XML doc comments, comments explain *why* only.
- Config: new keys are non-secret → `appsettings.json` only. Verified: `DockerCompose/docker-compose.yml` sets no `WyomingClient__*` env vars, so no compose change is needed. Deploy note (not a task): the pi5 prod compose `.env` may pin `WyomingClient__*`/`Satellites__*` values that shadow these defaults — check at rollout.
- Defaults (from spec): `FloorWindowMs` 3000, `EnterMarginDb` 9, `ExitMarginDb` 4, `PeakDropDb` 15, `MaxUtteranceMs` 40000 (prod appsettings), code-default `MaxUtteranceMs` stays 15000.
- `EndReason` string values (exact): `"trailing_silence"`, `"max_utterance"`, `"no_speech"`, `"forced"`.
- dB reference values used in tests: a constant-amplitude chunk of amplitude A has RMS = A; `ToDb`: 500 → 53.98, 2000 → 66.02, 3550 → 71.0, 3750 → 71.48, 8000 → 78.06, 24000 → 87.6. Enter bar over a 66 dB floor = 75.02 dB; exit bar = 70.02 dB.

---

### Task 1: AdaptiveLevelTracker

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/AdaptiveLevelTracker.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/AdaptiveLevelTrackerTests.cs` (create)

**Interfaces:**
- Consumes: nothing (pure class).
- Produces: `AdaptiveLevelTracker(double clampRms, double enterMarginDb, double exitMarginDb, double peakDropDb, TimeSpan floorWindow)` with `bool IsSpeech(double rms, double durationMs)`, `double FloorDb { get; }`, `double FloorRms { get; }`. Task 2 depends on this exact shape.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Wyoming/AdaptiveLevelTrackerTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AdaptiveLevelTrackerTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'AdaptiveLevelTracker' could not be found`. Capture this output — it is the RED evidence.

- [ ] **Step 3: Implement AdaptiveLevelTracker**

Create `McpChannelVoice/Services/WyomingProtocol/AdaptiveLevelTracker.cs` (no trailing newline):

```csharp
namespace McpChannelVoice.Services.WyomingProtocol;

// Speech/silence classification with measured references instead of a fixed bar.
// A fixed absolute RMS threshold encodes "the room noise floor is below X"; a TV in
// the room violates that permanently, so trailing silence never accumulates and the
// capture only ends at the max-utterance cap. A single far-field mic offers two
// measurable references:
//  - the noise floor: a windowed minimum of chunk levels, seeded by the first real
//    audio. It falls instantly when the room gets quieter (music duck engaging) and
//    rises only as quiet frames age out of the window (duck restore, TV scene
//    change), so the user's own speech cannot drag it up — word gaps and breaths
//    keep re-seeding the true background. Seeding from real audio (no grace period)
//    is deliberate: background above the clamp must read as silence from chunk one,
//    or it would latch minSpeech and end the turn before the user speaks.
//  - the utterance peak: near-field speech sits 15-25 dB above a far TV, so frames
//    far enough below the loudest speech of the turn are background regardless of
//    what the floor estimate believes. Armed only in the adaptive regime so it can
//    never clip loud-then-soft speech in a quiet room.
// The absolute threshold survives as a lower clamp: in a quiet room both hysteresis
// thresholds collapse to it, reproducing the legacy single-threshold gate exactly.
// dB ratios survive AGC gain shifts that absolute values don't.
public sealed class AdaptiveLevelTracker(
    double clampRms,
    double enterMarginDb,
    double exitMarginDb,
    double peakDropDb,
    TimeSpan floorWindow)
{
    private readonly double _clampDb = ToDb(clampRms);
    private readonly Queue<(double DurationMs, double RmsDb)> _window = new();
    private double _windowMs;
    private double _peakDb = double.NegativeInfinity;
    private bool _active;

    public double FloorDb { get; private set; }

    public double FloorRms => Math.Pow(10, FloorDb / 20);

    public bool IsSpeech(double rms, double durationMs)
    {
        var rmsDb = ToDb(rms);
        UpdateFloor(rmsDb, durationMs);
        _peakDb = Math.Max(_peakDb, rmsDb);

        // Two-threshold hysteresis: enter high, exit low. In a quiet room both
        // collapse to the clamp, reproducing the legacy single-threshold gate.
        var threshold = Math.Max(_clampDb, FloorDb + (_active ? exitMarginDb : enterMarginDb));
        var adaptiveRegime = FloorDb + enterMarginDb > _clampDb;
        _active = rmsDb >= threshold && !(adaptiveRegime && _peakDb - rmsDb > peakDropDb);
        return _active;
    }

    private void UpdateFloor(double rmsDb, double durationMs)
    {
        _window.Enqueue((durationMs, rmsDb));
        _windowMs += durationMs;
        while (_window.Count > 1 && _windowMs - _window.Peek().DurationMs >= floorWindow.TotalMilliseconds)
        {
            _windowMs -= _window.Dequeue().DurationMs;
        }
        FloorDb = _window.Min(e => e.RmsDb);
    }

    private static double ToDb(double rms) => 20 * Math.Log10(Math.Max(rms, 1));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AdaptiveLevelTrackerTests"`
Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/AdaptiveLevelTracker.cs Tests/Unit/McpChannelVoice/Wyoming/AdaptiveLevelTrackerTests.cs
git commit -m "feat(voice): add AdaptiveLevelTracker dual-reference speech classifier"
```

---

### Task 2: SilenceGate delegates to the tracker (+ all call sites compile)

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Modify: `McpChannelVoice/Settings/WyomingClientSettings.cs` (add the four knobs — call sites need them)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:233-238` (gate construction)
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs` (ctor + `Wrap` + internal gate)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs:80-81` (pass `settings.WyomingClient` to `Wrap`)
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs` (factories, seeding adjustments, new scenarios)
- Test: `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`, `Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs`, `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs` (constructor updates + leading-silence seeding, see Step 6)

**Interfaces:**
- Consumes: `AdaptiveLevelTracker` from Task 1.
- Produces: `SilenceGate(AdaptiveLevelTracker tracker, TimeSpan trailingSilence, TimeSpan maxUtterance, TimeSpan minSpeech, TimeSpan noSpeechTimeout = default)`; new members `double FloorRms { get; }` and `string? EndReason { get; }` (values `"trailing_silence"`/`"max_utterance"`/`"no_speech"`); `WyomingClientSettings` gains `int FloorWindowMs = 3000`, `double EnterMarginDb = 9`, `double ExitMarginDb = 4`, `double PeakDropDb = 15`; `SegmentedSpeechToText` ctor/`Wrap` gain a `WyomingClientSettings gateSettings` parameter (third position, before the logger). Tasks 3–5 rely on all of these.

**Semantic shift to internalize before editing tests:** a chunk stream whose FIRST chunk is loud seeds the floor at that loud level, so leading loud chunks read as silence until a quieter chunk arrives. Real captures always open with gap/ambient frames (the satellite pre-roll covers the wake detection-latency gap), so production behavior is unaffected — but synthetic tests that open with `Loud()` must gain one leading `Silent()` chunk to model reality. Tests where *every* chunk is loud (e.g. the max-utterance cap test) still pass unchanged: the floor equals the speech level, nothing classifies as speech, and the cap fires exactly as before.

- [ ] **Step 1: Write the failing tests**

In `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`:

Replace the two factories (lines 28-32 and 107-112):

```csharp
    private static AdaptiveLevelTracker Tracker() => new(
        clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
        floorWindow: TimeSpan.FromSeconds(3));

    private static SilenceGate NewGate() => new(
        Tracker(),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(2000),
        minSpeech: TimeSpan.FromMilliseconds(100));
```

```csharp
    private static SilenceGate FollowUpGate() => new(
        Tracker(),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(10_000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(500));
```

Adjust the four existing tests that open with `Loud()` (leading `Silent()` seeds the floor like the real pre-roll gap; all other expectations unchanged):

`Process_TrailingSilenceAfterSpeech_EndsUtterance` body becomes:

```csharp
        var gate = NewGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue); // pre-roll gap seeds the floor
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.EndUtterance);
```

`Process_BriefPauseBetweenSpeech_DoesNotEnd` body becomes:

```csharp
        var gate = NewGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue); // pre-roll gap seeds the floor
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);
        // Only one silent chunk since the last speech => trailing silence not yet reached.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
```

`SpeechElapsed_AccumulatesSpeechAndIgnoresSilence` body becomes:

```csharp
        var gate = NewGate();

        Feed(gate, Silent()); // pre-roll gap seeds the floor
        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Loud());   // 100 ms speech
        Feed(gate, Silent()); // silence — must not count

        gate.SpeechElapsed.ShouldBe(TimeSpan.FromMilliseconds(200));
```

`Process_OnlyBlipOfSpeechThenSilence_WaitsForMaxUtterance`: update its comment only — a leading loud chunk now seeds the floor and never counts as speech, which still must not end the turn:

```csharp
        // A capture opening directly on a loud chunk seeds the floor at that level, so
        // the blip never counts as speech at all — trailing silence must not end early.
```

(`Process_ExceedsMaxUtterance_EndsEvenWhileSpeaking`, all `FollowUpGate` tests, and both `PeakRms` tests keep their exact bodies: all-loud streams never classify as speech but still hit the cap; all-silent streams are unchanged; `PeakRms` measures raw RMS independent of classification.)

Add a `Tone` helper and the new scenarios:

```csharp
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
    private static SilenceGate BabbleGate(int noSpeechMs = 0) => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromMilliseconds(400)),
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests"`
Expected: build FAILS with `CS1729: 'SilenceGate' does not contain a constructor that takes...` plus missing-member errors for `EndReason`/`FloorRms`. Capture the output.

- [ ] **Step 3: Rework SilenceGate**

Replace `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` with:

```csharp
namespace McpChannelVoice.Services.WyomingProtocol;

// Server-side end-of-utterance detection for local-wake-word satellites.
//
// A WakeStreamingSatellite streams mic audio open-endedly after the wake word
// fires and only stops when it receives a Transcript back. There is no
// audio-stop to lean on, so the hub must decide when the speaker has finished:
// once speech has been observed, a run of trailing silence ends the utterance.
// What counts as speech vs silence is delegated to AdaptiveLevelTracker so a
// noisy room (TV, ducked music) raises the bar instead of pinning the capture
// open. A max-utterance cap bounds runaway streams; speech shorter than
// minSpeech is treated as noise and never ends the turn on its own.
public sealed class SilenceGate(
    AdaptiveLevelTracker tracker,
    TimeSpan trailingSilence,
    TimeSpan maxUtterance,
    TimeSpan minSpeech,
    TimeSpan noSpeechTimeout = default)
{
    private TimeSpan _elapsed;
    private TimeSpan _speechElapsed;
    private TimeSpan _trailingSilence;
    private bool _speechStarted;
    private double _peakRms;

    public enum Decision
    {
        Continue,
        EndUtterance,
        NoSpeech
    }

    public TimeSpan SpeechElapsed => _speechElapsed;

    public double PeakRms => _peakRms;

    public double FloorRms => tracker.FloorRms;

    public string? EndReason { get; private set; }

    public Decision Process(ReadOnlySpan<byte> pcm, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var duration = DurationOf(pcm.Length, sampleRateHz, sampleWidthBytes, channels);
        _elapsed += duration;

        var rms = Rms(pcm, sampleWidthBytes);
        _peakRms = Math.Max(_peakRms, rms);

        if (tracker.IsSpeech(rms, duration.TotalMilliseconds))
        {
            _speechStarted = true;
            _speechElapsed += duration;
            _trailingSilence = TimeSpan.Zero;
        }
        else if (_speechStarted)
        {
            _trailingSilence += duration;
            if (_speechElapsed > minSpeech && _trailingSilence >= trailingSilence)
            {
                EndReason = "trailing_silence";
                return Decision.EndUtterance;
            }
        }

        // The no-speech window expires unless MEANINGFUL speech (> minSpeech) has begun. Gating on
        // _speechElapsed rather than _speechStarted is deliberate: a sub-minSpeech blip (echo tail,
        // a cough) is noise by this gate's own definition and must not latch the window shut — else
        // the capture would hang open until the maxUtterance cap instead of timing out here.
        if (_speechElapsed <= minSpeech && noSpeechTimeout > TimeSpan.Zero && _elapsed >= noSpeechTimeout)
        {
            EndReason = "no_speech";
            return Decision.NoSpeech;
        }

        if (_elapsed >= maxUtterance)
        {
            EndReason = "max_utterance";
            return Decision.EndUtterance;
        }
        return Decision.Continue;
    }

    // Deliberately does NOT reset the tracker: SegmentedSpeechToText resets the gate per
    // phrase segment, and the learned noise floor must survive segment boundaries.
    public void Reset()
    {
        _elapsed = TimeSpan.Zero;
        _speechElapsed = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        _speechStarted = false;
        _peakRms = 0;
        EndReason = null;
    }

    private static TimeSpan DurationOf(int byteCount, int sampleRateHz, int sampleWidthBytes, int channels)
    {
        var bytesPerSecond = sampleRateHz * sampleWidthBytes * channels;
        return bytesPerSecond == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)byteCount / bytesPerSecond);
    }

    private static double Rms(ReadOnlySpan<byte> pcm, int sampleWidthBytes)
    {
        if (sampleWidthBytes != 2 || pcm.Length < 2)
        {
            return 0;
        }

        var samples = pcm.Length / 2;
        double sumSquares = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / samples);
    }
}
```

- [ ] **Step 4: Add the four knobs to WyomingClientSettings**

`McpChannelVoice/Settings/WyomingClientSettings.cs` becomes:

```csharp
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
}
```

- [ ] **Step 5: Update the three production call sites**

`McpChannelVoice/Services/WyomingSatelliteHost.cs` lines 233-238 (global settings for the new knobs; per-satellite resolution arrives in Task 3):

```csharp
                return session.OpenCapture(new SilenceGate(
                    new AdaptiveLevelTracker(
                        config.ResolveRmsThreshold(settings),
                        settings.EnterMarginDb,
                        settings.ExitMarginDb,
                        settings.PeakDropDb,
                        TimeSpan.FromMilliseconds(settings.FloorWindowMs)),
                    TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
                    TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
                    TimeSpan.FromMilliseconds(config.ResolveMinSpeechMs(settings)),
                    noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));
```

`McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`: primary constructor and `Wrap` gain `WyomingClientSettings gateSettings` (third parameter), and the gate construction in `TranscribeAsync` becomes:

```csharp
public sealed class SegmentedSpeechToText(
    ISpeechToText inner,
    SegmentedSttConfig config,
    WyomingClientSettings gateSettings,
    ILogger<SegmentedSpeechToText> logger) : ISpeechToText
```

```csharp
    public static ISpeechToText Wrap(
        ISpeechToText inner, SegmentedSttConfig config, WyomingClientSettings gateSettings, ILoggerFactory loggers) =>
        config.Enabled
            ? new SegmentedSpeechToText(inner, config, gateSettings, loggers.CreateLogger<SegmentedSpeechToText>())
            : inner;
```

```csharp
        var minSpeech = TimeSpan.FromMilliseconds(config.MinSegmentMs);
        var gate = new SilenceGate(
            new AdaptiveLevelTracker(
                config.SilenceRmsThreshold,
                gateSettings.EnterMarginDb,
                gateSettings.ExitMarginDb,
                gateSettings.PeakDropDb,
                TimeSpan.FromMilliseconds(gateSettings.FloorWindowMs)),
            TimeSpan.FromMilliseconds(config.SegmentSilenceMs),
            TimeSpan.MaxValue,
            minSpeech);
```

(add `using McpChannelVoice.Services.WyomingProtocol;` if not already present — it is, for `SilenceGate`.)

`McpChannelVoice/Modules/ConfigModule.cs` lines 80-81:

```csharp
            return McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, settings.WyomingClient, sp.GetRequiredService<ILoggerFactory>());
```

- [ ] **Step 6: Update the remaining test call sites**

`Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs` — factory (lines 23-28):

```csharp
    private static SilenceGate Gate(int noSpeechMs = 0) => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromSeconds(3)),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(5000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(noSpeechMs));
```

and `Feed_SpeechThenSilence_CompletesEndedAndExposesAudio` gains a leading silent chunk (floor seeding) with its replay count bumped 4 → 5:

```csharp
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        var count = 0;
        await foreach (var _ in capture.Audio)
        { count++; }
        count.ShouldBe(5);
```

`Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs` — factory (lines 12-15):

```csharp
    private static SilenceGate AnyGate(bool followUp) => new(
        new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5000),
        TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: followUp ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero);
```

`Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs` — factory (lines 54-55) and the two `Wrap` calls (lines 181-182, 191-192):

```csharp
    private static SegmentedSpeechToText New(ISpeechToText inner, SegmentedSttConfig? config = null) =>
        new(inner, config ?? Config(), new WyomingClientSettings(), NullLogger<SegmentedSpeechToText>.Instance);
```

```csharp
        var result = SegmentedSpeechToText.Wrap(
            inner, new SegmentedSttConfig { Enabled = false }, new WyomingClientSettings(), NullLoggerFactory.Instance);
```

```csharp
        var result = SegmentedSpeechToText.Wrap(
            inner, new SegmentedSttConfig { Enabled = true }, new WyomingClientSettings(), NullLoggerFactory.Instance);
```

**Leading-silence seeding rule** for the rest of `UtteranceCaptureTests`, `FollowUpConversationTests`, and `SegmentedSpeechToTextTests`: any test that feeds a stream whose FIRST chunk is `Loud()`/`Speech(n)` must prepend exactly one `Silent()` chunk (`Silence(1)` for segmented streams) — modeling the real pre-roll gap — and adjust count-based expectations by that one chunk (the segmented `FakeStt` returns received-chunk counts as `Text`, so first-segment counts increase by 1; `UtteranceCapture` replay counts increase by 1). Tests whose streams start silent, and tests that never assert on counts or decisions of the leading chunks, need no change. Apply the rule only to tests the suite run flags — do not restructure passing tests.

- [ ] **Step 7: Run the full voice unit suite to verify green**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: all pass. Iterate Step 6's seeding rule on any remaining failures; every fix must be explainable by the rule (a failure that isn't means a real bug — stop and diagnose the code instead).

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs McpChannelVoice/Settings/WyomingClientSettings.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): SilenceGate classifies speech via AdaptiveLevelTracker"
```

---

### Task 3: Per-satellite overrides + prod config

**Files:**
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs` (extend `GateSettings` + `Resolve*`)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:233-242` (use the resolvers)
- Modify: `McpChannelVoice/appsettings.json:4-10` (`MaxUtteranceMs` 40000 + new knobs)
- Test: `Tests/Unit/McpChannelVoice/SatelliteConfigTests.cs`

**Interfaces:**
- Consumes: `WyomingClientSettings` knobs from Task 2.
- Produces: `SatelliteConfig.ResolveFloorWindowMs/ResolveEnterMarginDb/ResolveExitMarginDb/ResolvePeakDropDb(WyomingClientSettings global)` — same pattern as the existing `ResolveRmsThreshold`.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/SatelliteConfigTests.cs` (match the file's existing style; `SatelliteConfig` requires `Identity` and `Room`):

```csharp
    [Fact]
    public void ResolveAdaptiveGateKnobs_WithOverrides_PreferSatelliteValues()
    {
        var global = new WyomingClientSettings();
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Office",
            Gate = new GateSettings
            {
                FloorWindowMs = 5000,
                EnterMarginDb = 12,
                ExitMarginDb = 6,
                PeakDropDb = 20
            }
        };

        config.ResolveFloorWindowMs(global).ShouldBe(5000);
        config.ResolveEnterMarginDb(global).ShouldBe(12);
        config.ResolveExitMarginDb(global).ShouldBe(6);
        config.ResolvePeakDropDb(global).ShouldBe(20);
    }

    [Fact]
    public void ResolveAdaptiveGateKnobs_WithoutOverrides_FallBackToGlobal()
    {
        var global = new WyomingClientSettings();
        var config = new SatelliteConfig { Identity = "household", Room = "Office" };

        config.ResolveFloorWindowMs(global).ShouldBe(3000);
        config.ResolveEnterMarginDb(global).ShouldBe(9);
        config.ResolveExitMarginDb(global).ShouldBe(4);
        config.ResolvePeakDropDb(global).ShouldBe(15);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteConfigTests"`
Expected: build FAILS with `CS1061: 'SatelliteConfig' does not contain a definition for 'ResolveFloorWindowMs'` (and siblings). Capture the output.

- [ ] **Step 3: Implement overrides**

In `McpChannelVoice/Settings/SatelliteConfig.cs`, after `ResolveMinSpeechMs` add:

```csharp
    public int ResolveFloorWindowMs(WyomingClientSettings global) =>
        Gate?.FloorWindowMs ?? global.FloorWindowMs;

    public double ResolveEnterMarginDb(WyomingClientSettings global) =>
        Gate?.EnterMarginDb ?? global.EnterMarginDb;

    public double ResolveExitMarginDb(WyomingClientSettings global) =>
        Gate?.ExitMarginDb ?? global.ExitMarginDb;

    public double ResolvePeakDropDb(WyomingClientSettings global) =>
        Gate?.PeakDropDb ?? global.PeakDropDb;
```

and extend `GateSettings`:

```csharp
public record GateSettings
{
    public double? SilenceRmsThreshold { get; init; }
    public int? MinSpeechMs { get; init; }
    public int? FloorWindowMs { get; init; }
    public double? EnterMarginDb { get; init; }
    public double? ExitMarginDb { get; init; }
    public double? PeakDropDb { get; init; }
}
```

In `McpChannelVoice/Services/WyomingSatelliteHost.cs` swap the four direct `settings.*` reads from Task 2 for the resolvers:

```csharp
                return session.OpenCapture(new SilenceGate(
                    new AdaptiveLevelTracker(
                        config.ResolveRmsThreshold(settings),
                        config.ResolveEnterMarginDb(settings),
                        config.ResolveExitMarginDb(settings),
                        config.ResolvePeakDropDb(settings),
                        TimeSpan.FromMilliseconds(config.ResolveFloorWindowMs(settings))),
                    TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
                    TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
                    TimeSpan.FromMilliseconds(config.ResolveMinSpeechMs(settings)),
                    noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));
```

In `McpChannelVoice/appsettings.json`, the `WyomingClient` block becomes (40 s runaway cap per user decision — per-capture, so every follow-up turn gets a fresh 40 s; knobs listed for discoverability):

```json
    "WyomingClient": {
        "ReconnectDelaySeconds": 5,
        "SilenceRmsThreshold": 700,
        "TrailingSilenceMs": 2000,
        "MaxUtteranceMs": 40000,
        "MinSpeechMs": 300,
        "FloorWindowMs": 3000,
        "EnterMarginDb": 9,
        "ExitMarginDb": 4,
        "PeakDropDb": 15
    },
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteConfigTests"`
Expected: all pass (existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/SatelliteConfigTests.cs
git commit -m "feat(voice): per-satellite adaptive gate overrides; 40s runaway cap"
```

---

### Task 4: Segment slicing keeps working under babble

**Files:**
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `SegmentedSpeechToText` with the Task 2 signature; its `FakeStt` returns received-chunk-count as `Text`.

- [ ] **Step 1: Write the behavioral pin test**

Add to `SegmentedSpeechToTextTests` (uses the file's existing `FakeStt`, `Stream`, `Speech`, `Config` helpers; `Tone` mirrors the SilenceGateTests helper but returns an `AudioChunk`):

```csharp
    private static AudioChunk Tone(short amplitude)
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };
    }

    private static IEnumerable<AudioChunk> Babble(int chunks) =>
        Enumerable.Range(0, chunks).Select(_ => Tone(2000));

    [Fact]
    public async Task TranscribeAsync_SpeechPhrasesOverBabble_StillSlicesSegments()
    {
        var inner = new FakeStt();
        // 400 ms floor window via the shared gate settings so the babble floor is
        // authoritative from the first chunks of this short synthetic stream.
        var sut = new SegmentedSpeechToText(
            inner, Config(), new WyomingClientSettings { FloorWindowMs = 400 },
            NullLogger<SegmentedSpeechToText>.Instance);

        // babble(8): floor converges at 2000; speech(6): a 600 ms phrase (> 500 ms
        // MinSegmentMs); babble(4): inter-phrase "silence" (>= 300 ms SegmentSilenceMs
        // closes the segment); second phrase; babble tail.
        var result = await sut.TranscribeAsync(
            Stream(Babble(8), Speech(6), Babble(4), Speech(6), Babble(4)),
            new TranscriptionOptions(), CancellationToken.None);

        // With the old fixed 500 threshold, babble RMS 2000 never reads as silence and
        // the whole stream decodes as ONE segment; adaptively it must slice at least twice.
        inner.Calls.ShouldBeGreaterThanOrEqualTo(2);
        result.Text.ShouldNotBeNullOrEmpty();
    }
```

- [ ] **Step 2: Run the new test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests.TranscribeAsync_SpeechPhrasesOverBabble_StillSlicesSegments"`
Expected: PASS (the wiring from Task 2 makes this hold). If it fails, hand-trace the dB arithmetic (babble 66.02 dB floor, enter bar 75.02 dB, speech 78.06 dB) before touching anything; a failure means either the chunk counts here are mis-stated (fix the test only if the trace proves the assertion wrong) or the production code has a real bug (fix the code).

- [ ] **Step 3: Run the whole segmented suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "test(voice): pin segment slicing over babble background"
```

---

### Task 5: FloorRms + EndReason through CaptureStats to metrics

**Files:**
- Modify: `McpChannelVoice/Services/UtteranceCapture.cs` (`CaptureStats`, `Stats`, `ForceEnd`)
- Modify: `Domain/DTOs/Metrics/VoiceEvent.cs` (two fields)
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs` (both publishes)
- Test: `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`, `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, `Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs`

**Interfaces:**
- Consumes: `SilenceGate.FloorRms`/`EndReason` from Task 2.
- Produces: `CaptureStats(double PeakRms, double FloorRms, long SpeechMs, string? EndReason)`; `VoiceEvent.FloorRms` (`double?`), `VoiceEvent.EndReason` (`string?`).

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`:

```csharp
    [Fact]
    public async Task Stats_AfterTrailingSilenceEnd_CarriesFloorAndEndReason()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent());
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.EndReason.ShouldBe("trailing_silence");
        capture.Stats.FloorRms.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Stats_AfterForceEnd_ReportsForced()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Silent());
        capture.Feed(Loud());

        capture.ForceEnd();

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.EndReason.ShouldBe("forced");
    }
```

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, update the two `CaptureStats` constructions and extend the assertions:

- In `DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats` (line 201): `new CaptureStats(PeakRms: 4200, FloorRms: 320, SpeechMs: 1800, EndReason: "trailing_silence"),` and after the `evt.SpeechMs.ShouldBe(1800);` line add:

```csharp
        evt.FloorRms.ShouldBe(320);
        evt.EndReason.ShouldBe("trailing_silence");
```

- In `DispatchAsync_Dropped_PublishesCaptureAndWhisperStats` (line 243): `new CaptureStats(PeakRms: 900, FloorRms: 610, SpeechMs: 450, EndReason: "max_utterance"),` and after the `evt.SpeechMs.ShouldBe(450);` line add:

```csharp
        evt.FloorRms.ShouldBe(610);
        evt.EndReason.ShouldBe("max_utterance");
```

In `Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs`, extend `VoiceEvent_RoundTripsThroughBaseType`: add `FloorRms = 320, EndReason = "trailing_silence"` to the object initializer and after the existing `DurationMs` assertion add:

```csharp
        ((VoiceEvent)decoded!).FloorRms.ShouldBe(320);
        ((VoiceEvent)decoded!).EndReason.ShouldBe("trailing_silence");
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtteranceCaptureTests|FullyQualifiedName~TranscriptDispatcherTests|FullyQualifiedName~VoiceEventTests"`
Expected: build FAILS (`CaptureStats` has no `FloorRms`/`EndReason`; `VoiceEvent` missing members). Capture the output.

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/UtteranceCapture.cs`:

```csharp
// Audio-level facts about one capture, published on UtteranceTranscribed metrics so the
// RMS/min-speech entry bar and the adaptive-floor margins can be tuned from real data
// instead of guesswork.
public readonly record struct CaptureStats(double PeakRms, double FloorRms, long SpeechMs, string? EndReason);
```

Add a `private bool _forced;` field, and:

```csharp
    public CaptureStats Stats => new(
        gate.PeakRms,
        gate.FloorRms,
        (long)gate.SpeechElapsed.TotalMilliseconds,
        _forced ? "forced" : gate.EndReason);
```

```csharp
    public void ForceEnd()
    {
        _forced = true;
        _chunks.Writer.TryComplete();
        _done.TrySetResult(CaptureOutcome.Ended);
    }
```

`Domain/DTOs/Metrics/VoiceEvent.cs` — after `SpeechMs` add:

```csharp
    public double? FloorRms { get; init; }
    public string? EndReason { get; init; }
```

`McpChannelVoice/Services/TranscriptDispatcher.cs` — in **both** `VoiceEvent` initializers (the "dropped" publish and the "dispatched" publish), after `SpeechMs = stats?.SpeechMs,` add:

```csharp
                    FloorRms = stats?.FloorRms,
                    EndReason = stats?.EndReason,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtteranceCaptureTests|FullyQualifiedName~TranscriptDispatcherTests|FullyQualifiedName~VoiceEventTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/UtteranceCapture.cs Domain/DTOs/Metrics/VoiceEvent.cs McpChannelVoice/Services/TranscriptDispatcher.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs
git commit -m "feat(voice): publish FloorRms and EndReason capture stats"
```

---

### Task 6: Full verification + spec amendment

**Files:**
- Modify: `docs/superpowers/specs/2026-07-20-adaptive-endpointing-design.md` (refinements section)

- [ ] **Step 1: Full build**

Run: `dotnet build agent.sln`
Expected: Build succeeded, 0 errors. (Warnings pre-exist; judge by type, not count.)

- [ ] **Step 2: Full unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`
Expected: all pass except documented pre-existing failures (a long-standing McpAgent cleanup test fails consistently regardless of this change — do not chase it; anything in `McpChannelVoice`/`Domain` namespaces must be green).

- [ ] **Step 3: Amend the spec with the planning refinements**

Append to `docs/superpowers/specs/2026-07-20-adaptive-endpointing-design.md`:

```markdown

## Refinements during planning (2026-07-20)

1. Floor smoothing: pure windowed minimum, no EMA — the min already provides
   instant-fall / window-delayed-rise; an EMA adds a constant without behavior.
2. The floor seeds from the first real chunks — no clamp-only grace period. A
   grace period would let above-clamp TV count as speech, latch minSpeech, and
   end the turn via trailing silence before the user speaks. Consequences:
   TV-only follow-up windows correctly time out as no_speech; on a TV-background
   wake turn the user must start speaking within the no-speech window (5 s), as
   today; a capture opening mid-loud-speech with zero leading gap reads that
   speech as floor until the first inter-word dip re-seeds the minimum (the
   satellite pre-roll's detection-latency gap supplies real gap frames).
3. Peak backstop armed only in the adaptive regime (floor + EnterMarginDb >
   clamp) so quiet-room loud-then-soft speech can never be clipped (Goal 3).
```

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-07-20-adaptive-endpointing-design.md
git commit -m "docs(voice): record adaptive endpointing planning refinements"
```

---

## Verification of spec coverage

| Spec requirement | Task |
|---|---|
| AdaptiveLevelTracker (floor, hysteresis, clamp, peak backstop) | 1 |
| SilenceGate delegation, unchanged state machine, EndReason | 2 |
| Follow-up windows stop capturing TV (`no_speech` timeout) | 2 (`Process_BabbleOnlyFollowUp_TimesOutAsNoSpeech`) |
| SegmentedSpeechToText same tracker, one tuning surface | 2, 4 |
| 40 s per-capture runaway cap | 3 |
| Per-satellite `GateSettings` overrides | 3 |
| `CaptureStats`/`VoiceEvent` FloorRms + EndReason metrics | 5 |
| Quiet-room zero-regression | 2 (existing tests, clamp collapse) |
| Duck step-down / step-up handling | 1 (`FallsImmediately`, `ConvergesAfterWindowTurnover`) |
| AGC robustness | 1 (`SlowGainRamp`) |
