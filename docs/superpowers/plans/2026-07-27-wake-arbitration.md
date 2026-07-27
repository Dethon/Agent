# Multi-Satellite Wake Arbitration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When several satellites hear the same "ok nabu" + command, exactly one — the loudest calibrated mic — processes it; losers silently re-arm, and a mid-conversation handoff moves the conversation to the winning satellite.

**Architecture:** The Rust satellite reports the wake word's RMS (from its pre-roll ring) plus score and source on the `run-pipeline` event and learns a silent `pause-satellite` abort. The .NET hub adds a `WakeArbiter` singleton that collects coincident wakes for a 500 ms window (Rule A: loudest calibrated RMS wins) and checks the winner against open captures on other satellites via onset alignment (Rule B: leak-suppress vs. handoff with a 6 dB steal margin). Conversation handoff re-binds the satellite↔conversation mapping.

**Tech Stack:** Rust (tokio, tract, serde_json) for `satellite/`; .NET 10 / xUnit / Shouldly / FakeTimeProvider for `McpChannelVoice`.

**Spec:** `docs/superpowers/specs/2026-07-27-wake-arbitration-design.md` — read it before starting.

## Global Constraints

- Branch: commit every task on the currently checked-out feature branch (`satellite-arbitration` at plan time). NEVER switch branches. `git add` explicit paths only.
- `.cs` files: NO trailing newline (`.editorconfig` `insert_final_newline = false`), file-scoped namespaces, primary constructors, records for DTOs, LINQ over loops, no XML doc comments, comments explain *why* only.
- Tests: Shouldly assertions, method naming `{Method}_{Scenario}_{ExpectedResult}`, unit tests in `Tests/Unit/`.
- `VoiceMetric` enum values are pinned wire integers: append ONLY — `WakeSuppressed = 29`, `WakeHandoff = 30`. Never renumber.
- Satellite invariants (satellite/CLAUDE.md): the main `select!` may only race `mpsc recv()` futures (no new compound I/O in the loop); the zero-lag pre-roll contract (`run-pipeline` → cue → pre-roll flush order) must not change; wire frames carry `data` once as the `data_length` body; playback is fixed 22 050 Hz (untouched here).
- Rust tests: `cd satellite && cargo test` (native target). Release builds only via `scripts/build-release.sh` (not needed for this plan).
- .NET: `dotnet build agent.sln`; unit tests via `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<ClassName>"`. Do not run concurrent dotnet test processes.
- TDD: every task is red → green. Run the failing test BEFORE implementing.
- Wire fields (exact names): `wake_rms` (f32), `wake_score` (f32), `source` (`"wake"` | `"button"`) on `run-pipeline` data; new hub→satellite event type `"pause-satellite"` with empty data.

---

### Task 1: Satellite — surface the wake score from `push_chunk`

**Files:**
- Modify: `satellite/src/wake/detector.rs` (`push_chunk` returns `Option<f32>`)
- Modify: `satellite/src/satellite/state_machine.rs:143-149` (call site)

**Interfaces:**
- Consumes: existing `WakeDetector::push_chunk(&mut self, chunk: &[i16]) -> bool`
- Produces: `WakeDetector::push_chunk(&mut self, chunk: &[i16]) -> Option<f32>` — `None` = no wake; `Some(score)` = wake fired with the classifier score of the triggering chunk. Task 2 consumes the score.

- [ ] **Step 1: Write the failing test** — in the `tests` module of `satellite/src/wake/detector.rs`:

```rust
#[test]
fn push_chunk_reports_score_on_wake() {
    let models = WakeModels::load().unwrap();
    let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
    let scores: Vec<f32> = wav("tests/fixtures/ok_nabu.wav")
        .chunks_exact(1280)
        .filter_map(|c| d.push_chunk(c))
        .collect();
    assert_eq!(scores.len(), 1, "exactly one wake from one utterance");
    assert!(scores[0] >= 0.5, "winning score is at least the threshold, got {}", scores[0]);
}
```

- [ ] **Step 2: Run it — expect a COMPILE failure** (`push_chunk` returns `bool`, `filter_map` needs `Option`):

Run: `cd satellite && cargo test push_chunk_reports_score_on_wake`
Expected: compile error `expected Option<_>, found bool` (that is the red state for a signature change).

- [ ] **Step 3: Implement** — in `detector.rs` change the signature and the two return paths:

```rust
/// Feed exactly 1280 samples (80 ms). Returns the classifier score when a wake fires.
pub fn push_chunk(&mut self, chunk: &[i16]) -> Option<f32> {
```

and at the end of the classifier stage (line ~148):

```rust
            if self.evaluate(score) { return Some(score); }
        }
        None
```

Update the existing detector tests to the new return type: in `fires_once_on_ok_nabu_then_respects_refractory` and `silent_on_silence` replace `if d.push_chunk(chunk) { fires += 1; }` with `if d.push_chunk(chunk).is_some() { fires += 1; }`; in `detectors_share_one_model_bundle` replace `.filter(|c| d.push_chunk(c))` with `.filter(|c| d.push_chunk(c).is_some())`.

Update the call site in `state_machine.rs` (keep behavior identical for now — Task 2 uses the score):

```rust
                            let fired = d.push_chunk(&samples);
                            tracing::debug!(us = t0.elapsed().as_micros() as u64, "wake inference");
                            if fired.is_some() {
```

- [ ] **Step 4: Run the full satellite test suite**

Run: `cd satellite && cargo test`
Expected: all tests pass, including the new one.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/wake/detector.rs satellite/src/satellite/state_machine.rs
git commit -m "feat(satellite): surface wake classifier score from push_chunk"
```

---

### Task 2: Satellite — wake metadata on `run-pipeline` (`wake_rms`, `wake_score`, `source`)

**Files:**
- Modify: `satellite/src/satellite/state_machine.rs` (`ring_rms`, `WakeSignal`, `start_turn`, wake + button paths)
- Modify: `satellite/src/wyoming/event.rs` (`PROTOCOL_VERSION` → `"1.3"`)

**Interfaces:**
- Consumes: `push_chunk -> Option<f32>` (Task 1), `bytes_to_samples(&[u8]) -> Vec<i16>` (`audio/capture.rs`), `WyomingEvent::with_data`, `Config::wake_preroll_chunks()`.
- Produces: `run-pipeline` data payload `{"source":"wake","wake_rms":<f32>,"wake_score":<f32>}` (wake) or `{"source":"button"}` (button). Hub Task 11 parses exactly these keys.

Ordering constraint: the wake path currently runs `trim_preroll` BEFORE `start_turn` — `ring_rms` must be computed BEFORE the trim, because the trim drops the wake-word audio it measures. RMS covers the ring EXCLUDING the newest `wake_preroll_chunks()` (the ~240 ms detection gap): the remaining ~800 ms is the span containing the wake word.

- [ ] **Step 1: Write the failing tests** — in the `tests` module of `state_machine.rs`:

```rust
    #[test]
    fn ring_rms_excludes_the_detection_gap_chunks() {
        // 3 old chunks at constant amplitude 100 (the wake word), 3 newest silent (the gap):
        // excluding the newest 3 must measure only the wake word.
        let loud: Vec<u8> = (0..1280).flat_map(|_| 100i16.to_le_bytes()).collect();
        let quiet: Vec<u8> = (0..1280).flat_map(|_| 0i16.to_le_bytes()).collect();
        let mut ring: VecDeque<Vec<u8>> = VecDeque::new();
        for _ in 0..3 { ring.push_back(loud.clone()); }
        for _ in 0..3 { ring.push_back(quiet.clone()); }
        let rms = ring_rms(&ring, 3);
        assert!((rms - 100.0).abs() < 0.01, "expected 100.0, got {rms}");
    }

    #[test]
    fn ring_rms_on_short_ring_is_zero() {
        let ring: VecDeque<Vec<u8>> = VecDeque::from(vec![vec![0u8; 2560]; 2]);
        assert_eq!(ring_rms(&ring, 3), 0.0);
    }

    #[tokio::test]
    async fn wake_turn_sends_run_pipeline_with_wake_metadata() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();
        let (playback, _done_rx, _pump) = pump();

        start_turn(&mut a, &mut mode, &ctx, &mut preroll, &playback,
            Some(WakeSignal { rms: 123.5, score: 0.87 })).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e.event_type, "run-pipeline");
        let data = e.data_obj();
        assert_eq!(data["source"], serde_json::json!("wake"));
        assert!((data["wake_rms"].as_f64().unwrap() - 123.5).abs() < 0.01);
        assert!((data["wake_score"].as_f64().unwrap() - 0.87).abs() < 0.001);
    }

    #[tokio::test]
    async fn button_turn_sends_run_pipeline_with_button_source() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<u8>> = VecDeque::new();
        let (playback, _done_rx, _pump) = pump();

        start_turn(&mut a, &mut mode, &ctx, &mut preroll, &playback, None).await.unwrap();

        let mut buf = BufReader::new(b);
        let e = read_event_buffered(&mut buf).await.unwrap().unwrap();
        assert_eq!(e.event_type, "run-pipeline");
        let data = e.data_obj();
        assert_eq!(data["source"], serde_json::json!("button"));
        assert!(!data.contains_key("wake_rms"));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd satellite && cargo test ring_rms`
Expected: compile error — `ring_rms` and `WakeSignal` not found, `start_turn` takes 5 args.

- [ ] **Step 3: Implement** in `state_machine.rs`:

Add near `trim_preroll`:

```rust
/// Loudness of the wake word as this satellite's mic heard it, for hub-side arbitration:
/// combined RMS (i16-amplitude units, hub-comparable) over the pre-roll ring EXCLUDING the
/// newest `exclude_newest` chunks — those are the detection-latency gap after the word ends.
/// Must run before trim_preroll, which drops exactly the audio this measures.
fn ring_rms(buf: &VecDeque<Vec<u8>>, exclude_newest: usize) -> f32 {
    let take = buf.len().saturating_sub(exclude_newest);
    let (sum_sq, n) = buf.iter().take(take).fold((0f64, 0usize), |(s, n), bytes| {
        let samples = bytes_to_samples(bytes);
        (s + samples.iter().map(|&v| v as f64 * v as f64).sum::<f64>(), n + samples.len())
    });
    if n == 0 { 0.0 } else { (sum_sq / n as f64).sqrt() as f32 }
}

struct WakeSignal {
    rms: f32,
    score: f32,
}
```

Change `start_turn` to take the signal and emit the data payload (the cue/pre-roll flush order is untouched):

```rust
async fn start_turn<W: AsyncWrite + Unpin>(
    wr: &mut W, mode: &mut Mode, ctx: &Ctx<'_>, preroll: &mut VecDeque<Vec<u8>>,
    playback: &PlaybackHandle, wake: Option<WakeSignal>,
) -> anyhow::Result<()> {
    let data = match &wake {
        Some(w) => json!({ "source": "wake", "wake_rms": w.rms, "wake_score": w.score }),
        None => json!({ "source": "button" }),
    };
    write_event(wr, &WyomingEvent::with_data("run-pipeline", data)).await?;
```

Wake path in `run_connection` (replace the `if fired.is_some()` block from Task 1):

```rust
                            if let Some(score) = fired {
                                info!("wake word detected");
                                let rms = ring_rms(&preroll, cfg.wake_preroll_chunks());
                                trim_preroll(&mut preroll, cfg.wake_preroll_chunks());
                                start_turn(&mut wr, &mut mode, &ctx, &mut preroll, &playback,
                                    Some(WakeSignal { rms, score })).await?;
                            }
```

Button path: `start_turn(&mut wr, &mut mode, &ctx, &mut preroll, &playback, None).await?;`

Update the two existing `start_turn` callers in tests (`start_turn_flushes_preroll_before_streaming`, `turn_lifecycle_publishes_led_states`, `audio_stop_during_streaming_turn_keeps_led_listening`) to pass `None` as the new last argument.

In `event.rs`: `pub const PROTOCOL_VERSION: &str = "1.3";` — then run `grep -rn '"1.2"' satellite/src/` and update any codec test that pins the old literal. The `data_obj()` helper now has a satellite-side caller path via tests; remove its `#[allow(dead_code)]` only if the compiler warns it is no longer needed.

- [ ] **Step 4: Run the full satellite suite**

Run: `cd satellite && cargo test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/satellite/state_machine.rs satellite/src/wyoming/event.rs satellite/src/wyoming/codec.rs
git commit -m "feat(satellite): report wake_rms/wake_score/source on run-pipeline"
```

---

### Task 3: Satellite — `pause-satellite` silent abort

**Files:**
- Modify: `satellite/src/satellite/state_machine.rs` (`handle_hub_event`)

**Interfaces:**
- Produces: hub→satellite `"pause-satellite"` (empty data): in `Mode::Streaming` → write `audio-stop {timestamp:0}` back, `Mode::Idle`, `detector.reset()`, **no cue**, LED → `Idle`. In `Mode::Idle` → no-op. Hub Task 11 sends this to arbitration losers.

- [ ] **Step 1: Write the failing tests** — in the `tests` module of `state_machine.rs`:

```rust
    // Arbitration loss: like transcript it stops streaming and re-arms wake, but SILENTLY —
    // no done cue (the user is talking to another satellite) and the LED goes straight to Idle.
    #[tokio::test]
    async fn pause_satellite_ends_streaming_silently_and_rearms() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, mut led_rx) = watch::channel(LedState::Listening);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Streaming;
        let (mut playback, _done_rx, _pump) = pump();

        let e = WyomingEvent::new("pause-satellite");
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert_eq!(read_event(&mut b).await.unwrap().unwrap().event_type, "audio-stop");
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle, "silent abort goes dark, not Thinking");
    }

    #[tokio::test]
    async fn pause_satellite_while_idle_is_a_noop() {
        let (mut a, b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, _done_rx, _pump) = pump();

        let e = WyomingEvent::new("pause-satellite");
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();

        assert_eq!(mode, Mode::Idle);
        assert!(!led_rx.has_changed().unwrap());
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(crate::wyoming::codec::read_event_buffered(&mut buf).await.unwrap().is_none());
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd satellite && cargo test pause_satellite`
Expected: FAIL — `pause-satellite` falls into the `other => warn!` arm, so mode stays `Streaming` in the first test.

- [ ] **Step 3: Implement** — add an arm in `handle_hub_event`, right after the `"transcript"` arm (note the differences from transcript: no done cue, LED `Idle` not `Thinking`):

```rust
        // Arbitration loss: another satellite won this utterance. End the capture like
        // transcript does, but silently — no done cue and straight to Idle, because from the
        // user's perspective this satellite was never part of the conversation.
        "pause-satellite" => {
            if *mode == Mode::Streaming {
                write_event(wr, &WyomingEvent::with_data("audio-stop", json!({"timestamp":0}))).await?;
                *mode = Mode::Idle;
                if let Some(d) = detector { d.reset(); }
                let _ = ctx.led.send(LedState::Idle);
            }
        }
```

- [ ] **Step 4: Run the full satellite suite**

Run: `cd satellite && cargo test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/satellite/state_machine.rs
git commit -m "feat(satellite): pause-satellite silent abort event"
```

---

### Task 4: Hub — `ArbitrationSettings` + `RmsOffsetDb` + appsettings

**Files:**
- Create: `McpChannelVoice/Settings/ArbitrationSettings.cs`
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs` (add `Arbitration`)
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs` (add `RmsOffsetDb`)
- Modify: `McpChannelVoice/appsettings.json` (add `Arbitration` block)
- Test: `Tests/Unit/McpChannelVoice/ArbitrationSettingsBindingTests.cs`

**Interfaces:**
- Produces: `ArbitrationSettings { bool Enabled=true, int WindowMs=500, double StealMarginDb=6, int DetectionLatencyMs=181, int WakeWordDurationMs=700, int AlignSlackMs=250, int QuietGapMs=400, TimeSpan HistorySpan }`; `VoiceSettings.Arbitration`; `SatelliteConfig.RmsOffsetDb` (double, default 0). Tasks 8/10/11 consume these.

Note on the repo config rule: these are non-secret settings — they live in `appsettings.json` (with env override via the standard binder paths, e.g. `Arbitration__WindowMs`, `Satellites__<id>__RmsOffsetDb`). No `DockerCompose/.env` entry (no secrets) and no compose `environment` additions, matching the existing precedent that voice tuning knobs are appsettings-only (compose carries no `Satellites__*` entries either).

- [ ] **Step 1: Write the failing test** — `Tests/Unit/McpChannelVoice/ArbitrationSettingsBindingTests.cs`:

```csharp
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ArbitrationSettingsBindingTests
{
    [Fact]
    public void Get_ArbitrationAndRmsOffset_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arbitration:Enabled"] = "false",
                ["Arbitration:WindowMs"] = "750",
                ["Arbitration:StealMarginDb"] = "3.5",
                ["Satellites:office:Identity"] = "household",
                ["Satellites:office:Room"] = "Office",
                ["Satellites:office:RmsOffsetDb"] = "-2.5"
            })
            .Build();

        var settings = config.Get<VoiceSettings>()!;

        settings.Arbitration.Enabled.ShouldBeFalse();
        settings.Arbitration.WindowMs.ShouldBe(750);
        settings.Arbitration.StealMarginDb.ShouldBe(3.5);
        settings.Satellites["office"].RmsOffsetDb.ShouldBe(-2.5);
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var s = new ArbitrationSettings();
        s.Enabled.ShouldBeTrue();
        s.WindowMs.ShouldBe(500);
        s.StealMarginDb.ShouldBe(6);
        s.DetectionLatencyMs.ShouldBe(181);
        s.WakeWordDurationMs.ShouldBe(700);
        s.AlignSlackMs.ShouldBe(250);
        s.QuietGapMs.ShouldBe(400);
        // History must cover the reconstructed wake-word span plus alignment slack and quiet gap.
        s.HistorySpan.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(181 + 700 + 250 + 400));
        new SatelliteConfig { Identity = "x", Room = "y" }.RmsOffsetDb.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ArbitrationSettingsBindingTests"`
Expected: compile FAIL — `ArbitrationSettings` does not exist.

- [ ] **Step 3: Implement**

`McpChannelVoice/Settings/ArbitrationSettings.cs`:

```csharp
namespace McpChannelVoice.Settings;

// Multi-satellite wake arbitration (docs/superpowers/specs/2026-07-27-wake-arbitration-design.md):
// several satellites hearing one utterance are resolved to a single winner by calibrated
// wake-word loudness. All timing knobs are hub receive-time; the wake-word span is
// reconstructed as [T_rx - DetectionLatencyMs - WakeWordDurationMs, T_rx - DetectionLatencyMs].
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
```

`VoiceSettings.cs` — add after `Tse`:

```csharp
    public ArbitrationSettings Arbitration { get; init; } = new();
```

`SatelliteConfig.cs` — add after `WakeWord`:

```csharp
    // Wake-arbitration loudness calibration in dB (env Satellites__<id>__RmsOffsetDb): added to
    // this satellite's reported wake_rms before cross-satellite comparison, so a hot mic doesn't
    // win every contest on gain alone. 0 = trust the hardware as-is.
    public double RmsOffsetDb { get; init; }
```

`McpChannelVoice/appsettings.json` — add a sibling of `"FollowUp"`:

```json
  "Arbitration": {
    "Enabled": true,
    "WindowMs": 500,
    "StealMarginDb": 6
  },
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ArbitrationSettingsBindingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/ArbitrationSettings.cs McpChannelVoice/Settings/VoiceSettings.cs McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/ArbitrationSettingsBindingTests.cs
git commit -m "feat(voice): arbitration settings and per-satellite RmsOffsetDb"
```

---

### Task 5: Hub — pinned metrics + `VoiceEvent` wake fields

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` (append `WakeSuppressed = 29`, `WakeHandoff = 30`)
- Modify: `Domain/DTOs/Metrics/VoiceEvent.cs` (add `WakeRms`, `WakeScore`)
- Test: `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`

**Interfaces:**
- Produces: `VoiceMetric.WakeSuppressed`, `VoiceMetric.WakeHandoff`; `VoiceEvent.WakeRms` (`double?`), `VoiceEvent.WakeScore` (`double?`). Tasks 10/11 emit them.

- [ ] **Step 1: Write the failing test** — append to `VoiceEnumsTests.cs`:

```csharp
    [Theory]
    [InlineData(VoiceMetric.WakeSuppressed, 29)]
    [InlineData(VoiceMetric.WakeHandoff, 30)]
    public void VoiceMetric_ArbitrationValues_ArePinned(VoiceMetric metric, int expected)
    {
        // Values persist as ints in Redis; a renumber silently re-labels historical data.
        ((int)metric).ShouldBe(expected);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: compile FAIL — members do not exist.

- [ ] **Step 3: Implement** — append to `VoiceMetric` (after `SpeakerVerifyEarlyMs = 28`):

```csharp
    // Multi-satellite wake arbitration: a co-heard wake that lost (Outcome carries why), and a
    // mid-conversation handoff where the conversation binding moved to the winning satellite.
    WakeSuppressed = 29,
    WakeHandoff = 30
```

Append to `VoiceEvent` (after `CompressionRatio`):

```csharp
    public double? WakeRms { get; init; }
    public double? WakeScore { get; init; }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceMetric.cs Domain/DTOs/Metrics/VoiceEvent.cs Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
git commit -m "feat(metrics): WakeSuppressed/WakeHandoff metrics and wake signal fields"
```

---

### Task 6: Hub — chunk history, capture abort, session plumbing

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/ChunkHistory.cs` (`ChunkSample`, `ChunkHistory`, `CaptureActivity`)
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` (`LastChunkRms`, `LastChunkWasSpeech`)
- Modify: `McpChannelVoice/Services/UtteranceCapture.cs` (`CaptureOutcome.Abandoned`, optional history, `Abort()`)
- Modify: `McpChannelVoice/Services/SatelliteSession.cs` (`OpenCapture` overload, `GetCaptureActivity`, `TryAbortCapture`, wake-signal stash, `SupportsPause`)
- Test: `Tests/Unit/McpChannelVoice/ChunkHistoryTests.cs`, additions to `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`

**Interfaces:**
- Consumes: `SilenceGate.Process`, `UtteranceCapture.Feed`, `SatelliteSession` capture fields, `FakeTimeProvider` (`Microsoft.Extensions.Time.Testing`).
- Produces (Tasks 8/10/11 consume all of these — exact signatures):
  - `public sealed record ChunkSample(long Timestamp, double Rms, bool IsSpeech);`
  - `public sealed class ChunkHistory(TimeProvider time, TimeSpan span)` with `long OpenedAt { get; }`, `void Record(double rms, bool isSpeech)`, `IReadOnlyList<ChunkSample> Snapshot()`
  - `public sealed record CaptureActivity(long OpenedAt, IReadOnlyList<ChunkSample> Samples);`
  - `SilenceGate.LastChunkRms` (`double`), `SilenceGate.LastChunkWasSpeech` (`bool`)
  - `CaptureOutcome.Abandoned`; `UtteranceCapture(SilenceGate gate, ChunkHistory? history = null)`; `ChunkHistory? History { get; }`; `bool Abort()` (true iff this call settled the capture)
  - `SatelliteSession.OpenCapture(SilenceGate gate, ChunkHistory? history = null)`; `CaptureActivity? GetCaptureActivity()`; `bool TryAbortCapture()`; `void NoteWakeSignal(double? rms, double? score)`; `(double? Rms, double? Score)? TryConsumeWakeSignal()`; `bool SupportsPause { get; }`; `void MarkSupportsPause()`

- [ ] **Step 1: Write the failing tests**

`Tests/Unit/McpChannelVoice/ChunkHistoryTests.cs`:

```csharp
using McpChannelVoice.Services.WyomingProtocol;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ChunkHistoryTests
{
    [Fact]
    public void Record_WithinSpan_SnapshotReturnsAllSamples()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromSeconds(2));

        history.Record(100, false);
        time.Advance(TimeSpan.FromMilliseconds(80));
        history.Record(900, true);

        var samples = history.Snapshot();
        samples.Count.ShouldBe(2);
        samples[0].Rms.ShouldBe(100);
        samples[0].IsSpeech.ShouldBeFalse();
        samples[1].IsSpeech.ShouldBeTrue();
        samples[1].Timestamp.ShouldBeGreaterThan(samples[0].Timestamp);
    }

    [Fact]
    public void Record_BeyondSpan_EvictsOldSamples()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromMilliseconds(500));

        history.Record(1, false);
        time.Advance(TimeSpan.FromMilliseconds(600));
        history.Record(2, true);

        var samples = history.Snapshot();
        samples.Count.ShouldBe(1);
        samples[0].Rms.ShouldBe(2);
    }

    [Fact]
    public void OpenedAt_IsStampedAtConstruction()
    {
        var time = new FakeTimeProvider();
        var expected = time.GetTimestamp();
        new ChunkHistory(time, TimeSpan.FromSeconds(1)).OpenedAt.ShouldBe(expected);
    }
}
```

Append to `UtteranceCaptureTests.cs` (mirror its existing gate/chunk construction helpers; if it lacks one, add this local helper):

```csharp
    private static SilenceGate LenientGate() => new(
        new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5000), TimeSpan.FromMilliseconds(100));

    [Fact]
    public void Abort_OpenCapture_SettlesAbandonedAndReturnsTrue()
    {
        var capture = new UtteranceCapture(LenientGate());

        capture.Abort().ShouldBeTrue();

        capture.Completed.IsCompletedSuccessfully.ShouldBeTrue();
        capture.Completed.Result.ShouldBe(CaptureOutcome.Abandoned);
    }

    [Fact]
    public void Abort_AlreadyEndedCapture_ReturnsFalseAndKeepsOutcome()
    {
        var capture = new UtteranceCapture(LenientGate());
        capture.ForceEnd();

        capture.Abort().ShouldBeFalse();

        capture.Completed.Result.ShouldBe(CaptureOutcome.Ended);
    }

    [Fact]
    public void Feed_WithHistory_RecordsGateVerdictPerChunk()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromSeconds(5));
        var capture = new UtteranceCapture(LenientGate(), history);

        capture.Feed(Chunk(3000)); // loud chunk: classified speech (clamp 500)
        capture.Feed(Chunk(0));    // silent chunk

        var samples = history.Snapshot();
        samples.Count.ShouldBe(2);
        samples[0].IsSpeech.ShouldBeTrue();
        samples[0].Rms.ShouldBe(3000, tolerance: 1);
        samples[1].IsSpeech.ShouldBeFalse();
    }

    private static AudioChunk Chunk(short amplitude, int samples = 1280)
    {
        var bytes = new byte[samples * 2];
        foreach (var i in Enumerable.Range(0, samples))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return new AudioChunk
        {
            Data = bytes,
            Format = new AudioFormat { SampleRateHz = 16000, SampleWidthBytes = 2, Channels = 1 },
            Timestamp = TimeSpan.Zero
        };
    }
```

(If `UtteranceCaptureTests.cs` already has an equivalent chunk builder, reuse it instead of adding `Chunk` — read the file first.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChunkHistoryTests|FullyQualifiedName~UtteranceCaptureTests"`
Expected: compile FAIL — `ChunkHistory`/`Abort`/`Abandoned` missing.

- [ ] **Step 3: Implement**

`ChunkHistory.cs`:

```csharp
namespace McpChannelVoice.Services.WyomingProtocol;

public sealed record ChunkSample(long Timestamp, double Rms, bool IsSpeech);

public sealed record CaptureActivity(long OpenedAt, IReadOnlyList<ChunkSample> Samples);

// Rolling per-chunk acoustic memory of one capture, so the wake arbiter can ask retrospectively
// "what did this mic hear during another satellite's wake-word span?". Written on the Wyoming
// read loop (Feed), snapshotted on the arbiter's decision task — hence the lock.
public sealed class ChunkHistory(TimeProvider time, TimeSpan span)
{
    private readonly Queue<ChunkSample> _samples = new();
    private readonly Lock _gate = new();

    public long OpenedAt { get; } = time.GetTimestamp();

    public void Record(double rms, bool isSpeech)
    {
        var now = time.GetTimestamp();
        var horizon = now - (long)(span.TotalSeconds * time.TimestampFrequency);
        lock (_gate)
        {
            _samples.Enqueue(new ChunkSample(now, rms, isSpeech));
            while (_samples.Count > 0 && _samples.Peek().Timestamp < horizon)
            {
                _samples.Dequeue();
            }
        }
    }

    public IReadOnlyList<ChunkSample> Snapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }
}
```

`SilenceGate.cs` — add two auto-properties and restructure the top of `Process` so the classification is observable:

```csharp
    public double LastChunkRms { get; private set; }
    public bool LastChunkWasSpeech { get; private set; }
```

```csharp
        var rms = Rms(pcm, sampleWidthBytes);
        _peakRms = Math.Max(_peakRms, rms);

        var isSpeech = tracker.IsSpeech(rms, duration.TotalMilliseconds);
        LastChunkRms = rms;
        LastChunkWasSpeech = isSpeech;

        if (isSpeech)
```

`UtteranceCapture.cs`:
- `public enum CaptureOutcome { Ended, NoSpeech, Abandoned }`
- primary constructor: `public sealed class UtteranceCapture(SilenceGate gate, ChunkHistory? history = null)`
- `public ChunkHistory? History => history;`
- in `Feed`, after `gate.Process(...)`: `history?.Record(gate.LastChunkRms, gate.LastChunkWasSpeech);`
- add:

```csharp
    // Arbitration loss/steal: settle as Abandoned so the conversation loop exits without
    // dispatching and without its own wire write (the arbiter owns the pause). Returns false
    // when the capture already ended naturally — the caller must then leave the turn alone.
    public bool Abort()
    {
        if (!_done.TrySetResult(CaptureOutcome.Abandoned))
        {
            return false;
        }
        _chunks.Writer.TryComplete();
        return true;
    }
```

`SatelliteSession.cs`:
- `OpenCapture` gains the optional history: `public UtteranceCapture OpenCapture(SilenceGate gate, ChunkHistory? history = null)` constructing `new UtteranceCapture(gate, history)`.
- add:

```csharp
    public CaptureActivity? GetCaptureActivity()
    {
        var capture = Volatile.Read(ref _capture);
        return capture?.History is { } history
            ? new CaptureActivity(history.OpenedAt, history.Snapshot())
            : null;
    }

    public bool TryAbortCapture() => Volatile.Read(ref _capture)?.Abort() ?? false;

    // Wake metadata stash: the read loop notes the claim's rms/score; the capture-open path
    // consumes it single-use onto the WakeTriggered event (same pattern as the dismissal stash).
    private readonly Lock _wakeSignalGate = new();
    private (double? Rms, double? Score)? _wakeSignal;

    public void NoteWakeSignal(double? rms, double? score)
    {
        lock (_wakeSignalGate)
        {
            _wakeSignal = (rms, score);
        }
    }

    public (double? Rms, double? Score)? TryConsumeWakeSignal()
    {
        lock (_wakeSignalGate)
        {
            var value = _wakeSignal;
            _wakeSignal = null;
            return value;
        }
    }

    // A connection that has ever reported wake_rms runs post-arbitration firmware and understands
    // pause-satellite; anything else gets the legacy transcript abort (audible done cue).
    public bool SupportsPause { get; private set; }
    public void MarkSupportsPause() => SupportsPause = true;
```

- [ ] **Step 4: Run to verify pass** (plus the neighbors that touch these types)

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChunkHistoryTests|FullyQualifiedName~UtteranceCaptureTests|FullyQualifiedName~SatelliteSession|FullyQualifiedName~FollowUpConversationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/ChunkHistory.cs McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs McpChannelVoice/Services/UtteranceCapture.cs McpChannelVoice/Services/SatelliteSession.cs Tests/Unit/McpChannelVoice/ChunkHistoryTests.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs
git commit -m "feat(voice): capture chunk history, abort path, and wake-signal stash"
```

---

### Task 7: Hub — `FollowUpConversation` abandoned exit

**Files:**
- Modify: `McpChannelVoice/Services/FollowUpConversation.cs` (`RunConversationAsync`)
- Test: `Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs`

**Interfaces:**
- Consumes: `CaptureOutcome.Abandoned` (Task 6).
- Produces: on an abandoned capture the loop exits with NO dispatch and NO `EndConversation` wire write (the arbiter owns the satellite's re-arm), and `_active` resets so the next wake works.

- [ ] **Step 1: Write the failing test** — append to `FollowUpConversationTests.cs` (uses the existing `Harness`):

```csharp
    [Fact]
    public async Task Abandoned_ArbitrationLoss_ExitsWithoutDispatchOrEnd()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].Abort().ShouldBeTrue(); // arbiter suppressed this satellite

        await Task.Delay(50);
        h.Dispatched.ShouldBeEmpty();
        h.Events.ShouldNotContain("end"); // the arbiter sends pause-satellite; no transcript here

        // the coordinator must be re-armed: a later wake starts a fresh conversation
        sut.OnWake();
        h.Opened.Count.ShouldBe(2);

        await StopAsync(sut, run);
    }
```

(`StopAsync` already exists at the bottom of the test class — reuse it.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"`
Expected: FAIL — today `Abandoned` falls through to the dispatch branch (`Dispatched` not empty) or hangs on the reply await.

- [ ] **Step 3: Implement** — in `RunConversationAsync`, right after `CloseCapture(capture);` and the `outcome is null` block:

```csharp
                if (outcome == CaptureOutcome.Abandoned)
                {
                    // Wake arbitration suppressed this turn (or handed it to another satellite).
                    // The arbiter already re-armed the satellite via pause-satellite, so no
                    // EndConversation here — writing a transcript would double-end the stream.
                    return;
                }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/FollowUpConversation.cs Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs
git commit -m "feat(voice): abandoned-capture exit path for arbitration losses"
```

---

### Task 8: Hub — pure arbitration rules

**Files:**
- Create: `McpChannelVoice/Services/WakeArbitrationRules.cs` (`WakeClaim`, `ArbitrationCandidate`, `WakeArbitrationRules`)
- Test: `Tests/Unit/McpChannelVoice/WakeArbitrationRulesTests.cs`

**Interfaces:**
- Consumes: `ChunkSample`, `CaptureActivity` (Task 6), `ArbitrationSettings` (Task 4).
- Produces (Task 10 consumes — exact signatures):
  - `public sealed record WakeClaim(string SatelliteId, double? WakeRms, double? WakeScore, string Source, long ReceivedAt);`
  - `public sealed record ArbitrationCandidate(WakeClaim Claim, double? CalibratedRms);`
  - `static ArbitrationCandidate PickWinner(IReadOnlyList<ArbitrationCandidate> candidates)`
  - `static (long Start, long End) WakeWordSpan(long receivedAt, long frequency, ArbitrationSettings settings)`
  - `static long MsToTicks(long ms, long frequency)`
  - `static bool HasAlignedOnset(CaptureActivity activity, long spanStart, long spanEnd, long frequency, ArbitrationSettings settings)`
  - `static double SpanPeakRms(CaptureActivity activity, long from, long to)`
  - `static double Calibrate(double rms, double offsetDb)`
  - `static bool CanSteal(double challengerCalibratedRms, double holderCalibratedPeak, double stealMarginDb)`

- [ ] **Step 1: Write the failing tests** — `Tests/Unit/McpChannelVoice/WakeArbitrationRulesTests.cs`. Use frequency `1000` (1 tick = 1 ms) so timestamps read as milliseconds:

```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WakeArbitrationRulesTests
{
    private const long Freq = 1000; // 1 tick == 1 ms in these tests
    private static readonly ArbitrationSettings Settings = new();

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
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, Settings);
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

    // ---- Rule B onset alignment. Span here: word start 9_119, word end 9_819 (from
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
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, Settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, Settings).ShouldBeTrue();
    }

    [Fact]
    public void HasAlignedOnset_MidSpeechSinceBeforeSpan_IsNotAligned()
    {
        // someone has been talking to this mic continuously since long before the span
        var activity = Activity(5_000,
            (8_600, 850, true), (8_700, 900, true), (8_800, 870, true), (8_900, 860, true),
            (9_000, 880, true), (9_200, 800, true), (9_300, 900, true));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, Settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, Settings).ShouldBeFalse();
    }

    [Fact]
    public void HasAlignedOnset_SilentAcrossSpan_IsNotAligned()
    {
        var activity = Activity(5_000,
            (9_000, 40, false), (9_200, 45, false), (9_500, 42, false));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, Settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, Settings).ShouldBeFalse();
    }

    [Fact]
    public void HasAlignedOnset_CaptureOpenedInSpanWithImmediateSpeech_IsAligned()
    {
        // follow-up window opened mid-span: no pre-span history exists to disprove an onset
        var activity = Activity(9_200, (9_250, 800, true), (9_330, 850, true));
        var (start, end) = WakeArbitrationRules.WakeWordSpan(10_000, Freq, Settings);
        WakeArbitrationRules.HasAlignedOnset(activity, start, end, Freq, Settings).ShouldBeTrue();
    }

    [Fact]
    public void SpanPeakRms_MaxWithinRangeZeroWhenEmpty()
    {
        var activity = Activity(0, (100, 500, true), (200, 900, true), (900, 9_999, true));
        WakeArbitrationRules.SpanPeakRms(activity, 50, 250).ShouldBe(900);
        WakeArbitrationRules.SpanPeakRms(activity, 300, 800).ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WakeArbitrationRulesTests"`
Expected: compile FAIL — types do not exist.

- [ ] **Step 3: Implement** — `McpChannelVoice/Services/WakeArbitrationRules.cs`:

```csharp
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record WakeClaim(
    string SatelliteId, double? WakeRms, double? WakeScore, string Source, long ReceivedAt);

public sealed record ArbitrationCandidate(WakeClaim Claim, double? CalibratedRms);

// The pure decision core of multi-satellite wake arbitration: no clocks, no I/O, no state —
// timestamps come in as TimeProvider ticks with an explicit frequency so every rule is testable
// with plain numbers. Policy source: docs/superpowers/specs/2026-07-27-wake-arbitration-design.md.
public static class WakeArbitrationRules
{
    // Rule A: button (deliberate physical intent) beats any wake; then loudest calibrated mic;
    // missing rms (legacy firmware) ranks below every reported value; final tie -> first heard.
    public static ArbitrationCandidate PickWinner(IReadOnlyList<ArbitrationCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.Claim.Source == "button" ? 1 : 0)
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
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WakeArbitrationRulesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WakeArbitrationRules.cs Tests/Unit/McpChannelVoice/WakeArbitrationRulesTests.cs
git commit -m "feat(voice): pure wake-arbitration decision rules"
```

---

### Task 9: Hub — `VoiceConversationManager.TransferBinding`

**Files:**
- Modify: `McpChannelVoice/Services/VoiceConversationManager.cs`
- Test: `Tests/Unit/McpChannelVoice/VoiceConversationManagerTests.cs`

**Interfaces:**
- Produces: `bool TransferBinding(string fromSatelliteId, string toSatelliteId)` — moves the conversation entry (fresh idle timer, updated reverse map); displaces any existing entry on the target; `false` when the source has no active conversation. Task 10 consumes it.

- [ ] **Step 1: Write the failing tests** — append to `VoiceConversationManagerTests.cs`, reusing its existing construction helpers/stubs (read the file first; it already stubs `IConversationFactory` and uses `FakeTimeProvider`):

```csharp
    [Fact]
    public async Task TransferBinding_ActiveConversation_MovesToTargetSatellite()
    {
        var (manager, _) = CreateManager(); // adapt to the file's existing factory helper name
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var conversationId = await manager.GetOrCreateAsync(sessionA, "agent", "hola", CancellationToken.None);

        manager.TransferBinding("sat-a", "sat-b").ShouldBeTrue();

        manager.GetActiveConversationId("sat-b").ShouldBe(conversationId);
        manager.GetActiveConversationId("sat-a").ShouldBeNull();
        manager.ResolveSatelliteId(conversationId).ShouldBe("sat-b");
    }

    [Fact]
    public void TransferBinding_NoActiveConversation_ReturnsFalse()
    {
        var (manager, _) = CreateManager();
        manager.TransferBinding("sat-a", "sat-b").ShouldBeFalse();
    }

    [Fact]
    public async Task TransferBinding_TargetHadItsOwnConversation_TargetEntryIsDisplaced()
    {
        var (manager, _) = CreateManager();
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var sessionB = new SatelliteSession("sat-b", new SatelliteConfig { Identity = "household", Room = "Office B" });
        var conversationA = await manager.GetOrCreateAsync(sessionA, "agent", "hola", CancellationToken.None);
        var conversationB = await manager.GetOrCreateAsync(sessionB, "agent", "hey", CancellationToken.None);

        manager.TransferBinding("sat-a", "sat-b").ShouldBeTrue();

        manager.GetActiveConversationId("sat-b").ShouldBe(conversationA);
        manager.ResolveSatelliteId(conversationB).ShouldBeNull();
    }
```

(Adapt `CreateManager()` to whatever helper the file actually uses — the assertions are the contract.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceConversationManagerTests"`
Expected: compile FAIL — `TransferBinding` missing.

- [ ] **Step 3: Implement** — add to `VoiceConversationManager`:

```csharp
    // Attention handoff: the user re-woke on another satellite mid-conversation, so the
    // conversation (and its idle timer) follows them. The displaced target entry — if the winner
    // had its own idle conversation — is simply dropped; it would have idle-expired anyway.
    public bool TransferBinding(string fromSatelliteId, string toSatelliteId)
    {
        lock (_gate)
        {
            if (!_bySatellite.Remove(fromSatelliteId, out var entry))
            {
                return false;
            }
            if (_bySatellite.Remove(toSatelliteId, out var displaced))
            {
                displaced.Timer.Dispose();
                _conversationToSatellite.Remove(displaced.ConversationId);
            }
            entry.Timer.Dispose();
            var generation = ++_generation;
            var timer = time.CreateTimer(
                _ => Expire(toSatelliteId, generation), null, lifetime, Timeout.InfiniteTimeSpan);
            _bySatellite[toSatelliteId] = entry with { Timer = timer, Generation = generation };
            _conversationToSatellite[entry.ConversationId] = toSatelliteId;
            logger.LogInformation(
                "Voice conversation {ConversationId} handed off {From} -> {To}",
                entry.ConversationId, fromSatelliteId, toSatelliteId);
            return true;
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceConversationManagerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/VoiceConversationManager.cs Tests/Unit/McpChannelVoice/VoiceConversationManagerTests.cs
git commit -m "feat(voice): conversation handoff via TransferBinding"
```

---

### Task 10: Hub — `WakeArbiter` service

**Files:**
- Create: `McpChannelVoice/Services/WakeArbiter.cs` (`WakeArbiterHandle`, `WakeArbiter`)
- Test: `Tests/Unit/McpChannelVoice/WakeArbiterTests.cs`

**Interfaces:**
- Consumes: Tasks 4, 5, 6, 8, 9 outputs; `IMetricsPublisher.PublishAsync(MetricEvent, CancellationToken)`.
- Produces (Task 11 consumes):
  - `public sealed record WakeArbiterHandle(SatelliteSession Session, Func<CancellationToken, Task> PauseAsync, Func<CancellationToken, Task> EndLegacyAsync);`
  - `void Register(string satelliteId, WakeArbiterHandle handle)` / `void Unregister(string satelliteId)`
  - `void Claim(string satelliteId, double? wakeRms, double? wakeScore, string source)` — never blocks the read loop; schedules the decision.

Behavior contract:
- Disabled or fewer than 2 registered handles → `Claim` is a no-op (today's behavior).
- First claim opens a window; claims within `WindowMs` join (duplicates by satelliteId ignored); after `WindowMs` (via `Task.Delay(..., time)` so `FakeTimeProvider` drives tests) the decision runs.
- Rule A: `PickWinner` over calibrated candidates; every loser: `Session.TryAbortCapture()` + (`SupportsPause` ? `PauseAsync` : `EndLegacyAsync`) + `WakeSuppressed` metric (`Outcome = "lost_loudness"`, `WakeRms`/`WakeScore` from its claim).
- Rule B (skipped when the winner's `Source == "button"`): among registered satellites that are NOT candidates and have `GetCaptureActivity() != null`, find aligned holders via `HasAlignedOnset` over the winner's `WakeWordSpan`; take the loudest by calibrated `SpanPeakRms` over `[spanStart − slack, spanEnd + slack]`.
  - No aligned holder → winner proceeds (nothing to do).
  - Winner has no `WakeRms` (legacy) or `CanSteal` false → suppress the winner too (`Outcome = "leak"`).
  - `CanSteal` true → handoff: `holder.Session.TryAbortCapture()`; **only if it returns true** → pause/legacy wire to the holder + `conversations.TransferBinding(holderId, winnerId)` + `WakeHandoff` metric (`SatelliteId = winnerId`, `Outcome = holderId`); if `TryAbortCapture` returned false the capture already ended naturally — do nothing (independent-turns edge from the spec).
- All exceptions inside the decision are caught and logged; the window slot is always cleared.

- [ ] **Step 1: Write the failing tests** — `Tests/Unit/McpChannelVoice/WakeArbiterTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WakeArbiterTests
{
    private sealed class ListPublisher : IMetricsPublisher
    {
        public readonly List<MetricEvent> Events = [];
        public Task PublishAsync(MetricEvent evt, CancellationToken ct)
        {
            lock (Events) { Events.Add(evt); }
            return Task.CompletedTask;
        }
    }

    private sealed class SatelliteHarness
    {
        public readonly SatelliteSession Session;
        public int Paused;
        public int LegacyEnded;
        public UtteranceCapture? Capture;

        public SatelliteHarness(string id, string room, double offsetDb = 0)
        {
            Session = new SatelliteSession(id, new SatelliteConfig
            {
                Identity = "household", Room = room, RmsOffsetDb = offsetDb
            });
        }

        public WakeArbiterHandle Handle => new(
            Session,
            _ => { Interlocked.Increment(ref Paused); return Task.CompletedTask; },
            _ => { Interlocked.Increment(ref LegacyEnded); return Task.CompletedTask; });

        public void OpenCapture(FakeTimeProvider time, ArbitrationSettings settings)
        {
            var gate = new SilenceGate(
                new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
                TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Capture = Session.OpenCapture(gate, new ChunkHistory(time, settings.HistorySpan));
        }
    }

    private static (WakeArbiter Arbiter, FakeTimeProvider Time, ListPublisher Metrics,
        VoiceConversationManager Conversations) Create(ArbitrationSettings? settings = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var metrics = new ListPublisher();
        // reuse the IConversationFactory stub + accumulator setup from VoiceConversationManagerTests
        var conversations = TestConversationManager(time);
        var arbiter = new WakeArbiter(
            settings ?? new ArbitrationSettings(), conversations, metrics, time,
            NullLogger<WakeArbiter>.Instance);
        return (arbiter, time, metrics, conversations);
    }

    // TestConversationManager: do NOT invent a stub — open VoiceConversationManagerTests.cs and
    // copy its exact IConversationFactory fake + ReplyTextAccumulator construction into a private
    // static helper here (same types, same NullLogger). The contract this file needs from it:
    // GetOrCreateAsync returns a stable conversation id per satellite, TransferBinding works.

    private static async Task SettleAsync(FakeTimeProvider time, int windowMs)
    {
        // let DecideAfterWindowAsync reach its Task.Delay, then fire it, then let it run
        await Task.Delay(50);
        time.Advance(TimeSpan.FromMilliseconds(windowMs + 1));
        await Task.Delay(50);
    }

    [Fact]
    public async Task Claim_TwoCoincidentWakes_LouderWinsQuieterIsPausedWithoutDispatch()
    {
        var (arbiter, time, metrics, _) = Create();
        var near = new SatelliteHarness("near", "Office A");
        var far = new SatelliteHarness("far", "Office B");
        arbiter.Register("near", near.Handle);
        arbiter.Register("far", far.Handle);
        near.Session.MarkSupportsPause();
        far.Session.MarkSupportsPause();
        near.OpenCapture(time, new ArbitrationSettings());
        far.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("far", 200, 0.8, "wake");
        arbiter.Claim("near", 900, 0.9, "wake");
        await SettleAsync(time, 500);

        far.Paused.ShouldBe(1);
        near.Paused.ShouldBe(0);
        far.Capture!.Completed.IsCompleted.ShouldBeTrue();
        far.Capture.Completed.Result.ShouldBe(CaptureOutcome.Abandoned);
        near.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).SatelliteId.ShouldBe("far");
    }

    [Fact]
    public async Task Claim_RmsOffsetCalibration_FlipsTheWinner()
    {
        var (arbiter, time, _, _) = Create();
        var hot = new SatelliteHarness("hot-mic", "A");             // louder raw, no offset
        var calibrated = new SatelliteHarness("quiet-mic", "B", offsetDb: 12); // +12 dB ~= x3.98
        arbiter.Register("hot-mic", hot.Handle);
        arbiter.Register("quiet-mic", calibrated.Handle);
        hot.Session.MarkSupportsPause();
        calibrated.Session.MarkSupportsPause();
        hot.OpenCapture(time, new ArbitrationSettings());
        calibrated.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("hot-mic", 300, null, "wake");
        arbiter.Claim("quiet-mic", 100, null, "wake"); // 100 * 3.98 = 398 > 300
        await SettleAsync(time, 500);

        hot.Paused.ShouldBe(1);
        calibrated.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_SingleRegisteredSatellite_IsANoOp()
    {
        var (arbiter, time, metrics, _) = Create();
        var only = new SatelliteHarness("only", "A");
        arbiter.Register("only", only.Handle);
        only.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("only", 500, null, "wake");
        await SettleAsync(time, 500);

        only.Paused.ShouldBe(0);
        only.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_LegacyLoserWithoutRms_GetsTranscriptFallback()
    {
        var (arbiter, time, _, _) = Create();
        var legacy = new SatelliteHarness("legacy", "A"); // never MarkSupportsPause
        var modern = new SatelliteHarness("modern", "B");
        arbiter.Register("legacy", legacy.Handle);
        arbiter.Register("modern", modern.Handle);
        modern.Session.MarkSupportsPause();
        legacy.OpenCapture(time, new ArbitrationSettings());
        modern.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("legacy", null, null, "wake");
        arbiter.Claim("modern", 400, null, "wake");
        await SettleAsync(time, 500);

        legacy.LegacyEnded.ShouldBe(1);
        legacy.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_FreshWakeVsQuietOpenHolder_BothProceed()
    {
        // Holder's open capture heard nothing during the wake span -> independent utterances.
        var (arbiter, time, metrics, _) = Create();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        holder.OpenCapture(time, new ArbitrationSettings());
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromSeconds(2));
        waker.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("waker", 600, null, "wake");
        await SettleAsync(time, 500);

        holder.Paused.ShouldBe(0);
        waker.Paused.ShouldBe(0);
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_AlignedLouderHolder_SuppressesTheFreshWakeAsLeak()
    {
        var (arbiter, time, metrics, _) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();

        holder.OpenCapture(time, settings);
        // quiet history, then loud speech exactly at the wake word instant
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(4000));   // the wake word as heard by the holder's mic
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 300, null, "wake"); // far away: much quieter than the holder heard
        await SettleAsync(time, settings.WindowMs);

        waker.Paused.ShouldBe(1);
        holder.Paused.ShouldBe(0);
        holder.Capture.Completed.IsCompleted.ShouldBeFalse("the holder keeps its capture");
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).Outcome.ShouldBe("leak");
    }

    [Fact]
    public async Task Claim_AlignedMuchLouderFreshWake_HandsOffTheConversation()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        var conversationId = await conversations.GetOrCreateAsync(
            holder.Session, "agent", "hola", CancellationToken.None);

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));    // faint leak of the wake word said far from A
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // user is right next to B: > 6 dB louder
        await SettleAsync(time, settings.WindowMs);

        holder.Paused.ShouldBe(1);
        holder.Capture.Completed.Result.ShouldBe(CaptureOutcome.Abandoned);
        waker.Paused.ShouldBe(0);
        conversations.GetActiveConversationId("waker").ShouldBe(conversationId);
        conversations.GetActiveConversationId("holder").ShouldBeNull();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeHandoff).SatelliteId.ShouldBe("waker");
    }

    private static AudioChunk SilentChunk() => PcmChunk(0);
    private static AudioChunk LoudChunk(short amplitude) => PcmChunk(amplitude);

    private static AudioChunk PcmChunk(short amplitude, int samples = 1280)
    {
        var bytes = new byte[samples * 2];
        foreach (var i in Enumerable.Range(0, samples))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return new AudioChunk
        {
            Data = bytes,
            Format = new AudioFormat { SampleRateHz = 16000, SampleWidthBytes = 2, Channels = 1 },
            Timestamp = TimeSpan.Zero
        };
    }
}
```

Implementation note for `TestConversationManager`: copy the exact `IConversationFactory` stub + `ReplyTextAccumulator` construction already used by `VoiceConversationManagerTests.cs` (read that file; do not invent a new stub). The `SettleAsync` helper's small real delays are the same pattern `FollowUpConversationTests` uses around `FakeTimeProvider.Advance`.

Timing note for the Rule B tests: `FakeTimeProvider` starts all harnesses on one clock; `ChunkHistory.Record` stamps at `Feed` time, and the claim stamps at `Claim` time — the advances between them build the timeline the assertions rely on (silence, speech at the word instant, then `DetectionLatencyMs + 700` later the claim arrives, putting the reconstructed span over the loud chunk).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WakeArbiterTests"`
Expected: compile FAIL — `WakeArbiter`/`WakeArbiterHandle` missing.

- [ ] **Step 3: Implement** — `McpChannelVoice/Services/WakeArbiter.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record WakeArbiterHandle(
    SatelliteSession Session,
    Func<CancellationToken, Task> PauseAsync,
    Func<CancellationToken, Task> EndLegacyAsync);

// Cross-satellite wake arbitration seat (spec: docs/superpowers/specs/
// 2026-07-27-wake-arbitration-design.md). Claims arrive synchronously on each connection's
// Wyoming read loop; the decision runs later on its own task, so the read loops never wait.
// Every claimant has already opened its capture — losing costs a discarded capture, never audio.
public sealed class WakeArbiter(
    ArbitrationSettings settings,
    VoiceConversationManager conversations,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<WakeArbiter> logger)
{
    private readonly Dictionary<string, WakeArbiterHandle> _handles = new();
    private readonly Lock _gate = new();
    private List<WakeClaim>? _window;

    public void Register(string satelliteId, WakeArbiterHandle handle)
    {
        lock (_gate)
        {
            _handles[satelliteId] = handle;
        }
    }

    public void Unregister(string satelliteId)
    {
        lock (_gate)
        {
            _handles.Remove(satelliteId);
        }
    }

    public void Claim(string satelliteId, double? wakeRms, double? wakeScore, string source)
    {
        if (!settings.Enabled)
        {
            return;
        }
        lock (_gate)
        {
            if (_handles.Count < 2)
            {
                return;
            }
            var claim = new WakeClaim(satelliteId, wakeRms, wakeScore, source, time.GetTimestamp());
            if (_window is not null)
            {
                if (_window.All(c => c.SatelliteId != satelliteId))
                {
                    _window.Add(claim);
                }
                return;
            }
            _window = [claim];
        }
        _ = DecideAfterWindowAsync();
    }

    private async Task DecideAfterWindowAsync()
    {
        List<WakeClaim>? claims = null;
        Dictionary<string, WakeArbiterHandle> handles;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(settings.WindowMs), time);
            lock (_gate)
            {
                claims = _window;
                _window = null;
                handles = new Dictionary<string, WakeArbiterHandle>(_handles);
            }
            if (claims is not null)
            {
                await DecideAsync(claims, handles);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wake arbitration decision failed for {Claims}",
                string.Join(", ", (claims ?? []).Select(c => c.SatelliteId)));
            lock (_gate)
            {
                _window = null;
            }
        }
    }

    private async Task DecideAsync(List<WakeClaim> claims, Dictionary<string, WakeArbiterHandle> handles)
    {
        var candidates = claims
            .Where(c => handles.ContainsKey(c.SatelliteId))
            .Select(c => new ArbitrationCandidate(c, c.WakeRms is { } rms
                ? WakeArbitrationRules.Calibrate(rms, handles[c.SatelliteId].Session.Config.RmsOffsetDb)
                : null))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var winner = WakeArbitrationRules.PickWinner(candidates);
        foreach (var loser in candidates.Where(c => !ReferenceEquals(c, winner)))
        {
            await SuppressAsync(handles[loser.Claim.SatelliteId], loser.Claim, "lost_loudness");
        }

        if (winner.Claim.Source == "button")
        {
            return; // deliberate physical intent: never suppressed, never a leak
        }

        var frequency = time.TimestampFrequency;
        var (spanStart, spanEnd) = WakeArbitrationRules.WakeWordSpan(
            winner.Claim.ReceivedAt, frequency, settings);
        var slack = WakeArbitrationRules.MsToTicks(settings.AlignSlackMs, frequency);
        var holder = handles
            .Where(kv => claims.All(c => c.SatelliteId != kv.Key))
            .Select(kv => (kv.Key, Handle: kv.Value, Activity: kv.Value.Session.GetCaptureActivity()))
            .Where(h => h.Activity is not null && WakeArbitrationRules.HasAlignedOnset(
                h.Activity!, spanStart, spanEnd, frequency, settings))
            .Select(h => (h.Key, h.Handle, Peak: WakeArbitrationRules.Calibrate(
                WakeArbitrationRules.SpanPeakRms(h.Activity!, spanStart - slack, spanEnd + slack),
                h.Handle.Session.Config.RmsOffsetDb)))
            .OrderByDescending(h => h.Peak)
            .Select(h => ((string, WakeArbiterHandle, double)?)h)
            .FirstOrDefault();
        if (holder is not { } aligned)
        {
            return; // no other mic heard this utterance: the winner just proceeds
        }

        var (holderId, holderHandle, holderPeak) = aligned;
        if (winner.CalibratedRms is { } challenger
            && WakeArbitrationRules.CanSteal(challenger, holderPeak, settings.StealMarginDb))
        {
            // Only a capture we actually aborted may be stolen from: if it already ended
            // naturally, its dispatch is in flight and these were independent turns.
            if (!holderHandle.Session.TryAbortCapture())
            {
                return;
            }
            await SendReArmAsync(holderHandle);
            conversations.TransferBinding(holderId, winner.Claim.SatelliteId);
            await PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.WakeHandoff,
                SatelliteId = winner.Claim.SatelliteId,
                Room = handles[winner.Claim.SatelliteId].Session.Config.Room,
                Identity = handles[winner.Claim.SatelliteId].Session.Config.Identity,
                Outcome = holderId,
                WakeRms = winner.Claim.WakeRms,
                WakeScore = winner.Claim.WakeScore
            });
            return;
        }

        // The wake word leaked into the holder's already-open mic and the holder heard it
        // louder (or the challenger can't prove otherwise): the holder keeps the turn.
        await SuppressAsync(handles[winner.Claim.SatelliteId], winner.Claim, "leak");
    }

    private async Task SuppressAsync(WakeArbiterHandle handle, WakeClaim claim, string outcome)
    {
        if (!handle.Session.TryAbortCapture())
        {
            logger.LogWarning(
                "Arbitration loser {Id} had no abortable capture (ended early); letting it proceed",
                claim.SatelliteId);
            return;
        }
        await SendReArmAsync(handle);
        await PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.WakeSuppressed,
            SatelliteId = claim.SatelliteId,
            Room = handle.Session.Config.Room,
            Identity = handle.Session.Config.Identity,
            Outcome = outcome,
            WakeRms = claim.WakeRms,
            WakeScore = claim.WakeScore
        });
    }

    private static Task SendReArmAsync(WakeArbiterHandle handle) =>
        handle.Session.SupportsPause
            ? handle.PauseAsync(CancellationToken.None)
            : handle.EndLegacyAsync(CancellationToken.None);

    private async Task PublishAsync(VoiceEvent evt)
    {
        try
        {
            await metrics.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish {Metric}", evt.Metric);
        }
    }
}
```

Wire-write failure note: `SendReArmAsync` is awaited inside `DecideAsync`, whose exceptions land in `DecideAfterWindowAsync`'s catch — a dead loser connection cannot fault the arbiter permanently.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WakeArbiterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WakeArbiter.cs Tests/Unit/McpChannelVoice/WakeArbiterTests.cs
git commit -m "feat(voice): WakeArbiter window arbitration and handoff"
```

---

### Task 11: Hub — host read-loop and DI integration

**Files:**
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (ctor param, read loop, handle registration, capture history, WakeTriggered enrichment)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (DI)
- Modify: `McpChannelVoice/Services/JsonNumber.cs` only if it lacks a nullable `ReadDouble(JsonObject, string)` — it should already have one (used by `OpenAiSpeechToText`).
- Test: extend `Tests/Unit/McpChannelVoice/ConfigModuleTests.cs` (DI resolution), plus the end-to-end coverage lands in Task 12.

**Interfaces:**
- Consumes: everything produced by Tasks 4–10.
- Produces: the live wiring —
  1. `WyomingSatelliteHost` primary constructor gains `WakeArbiter arbiter` (before the optional `speakerVerifier`).
  2. `RunConnectionAsync` registers the handle after `sessionRegistry.Register(session)`:

```csharp
        arbiter.Register(id, new WakeArbiterHandle(
            session,
            ct2 => client.WriteAsync(WyomingEvent.Header("pause-satellite", new JsonObject()), ct2),
            ct2 => client.WriteAsync(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), ct2)));
```

  and in the `finally`, before `sessionRegistry.Unregister(id)`: `arbiter.Unregister(id);`
  (Write-concurrency note: `client.WriteAsync` is already invoked concurrently today from the playback loop and the coordinator's `EndConversation`; the arbiter's writes add a third caller on the same, already-shared writer — same guarantees as today, no new mechanism.)
  3. Read loop wake case becomes:

```csharp
                    case "run-pipeline":
                    case "audio-start":
                        NoteDismissals(session, alerts.Acknowledge(id));
                        var wakeRms = JsonNumber.ReadDouble(evt.Data, "wake_rms");
                        var wakeScore = JsonNumber.ReadDouble(evt.Data, "wake_score");
                        var source = evt.Data["source"]?.GetValue<string>() ?? "wake";
                        if (wakeRms is not null)
                        {
                            session.MarkSupportsPause();
                        }
                        session.NoteWakeSignal(wakeRms, wakeScore);
                        arbiter.Claim(id, wakeRms, wakeScore, source);
                        coordinator.OnWake();
                        break;
```

  4. `BuildCoordinator`'s `OpenCapture` closure: attach a history and enrich WakeTriggered:

```csharp
                if (!isFollowUp)
                {
                    var wake = session.TryConsumeWakeSignal();
                    PublishVoiceMetric(VoiceMetric.WakeTriggered, session,
                        wakeRms: wake?.Rms, wakeScore: wake?.Score);
                }
                ...
                return session.OpenCapture(new SilenceGate(...unchanged...),
                    new ChunkHistory(time, voiceSettings.Arbitration.HistorySpan));
```

  and `PublishVoiceMetric` gains two optional params flowing onto the event:

```csharp
    private void PublishVoiceMetric(
        VoiceMetric metric, SatelliteSession session, CaptureStats? stats = null,
        double? wakeRms = null, double? wakeScore = null) =>
        _ = SafePublishAsync(new VoiceEvent
        {
            ...existing fields...,
            WakeRms = wakeRms,
            WakeScore = wakeScore
        });
```

  5. `ConfigModule.ConfigureVoiceChannel`: `.AddSingleton(settings.Arbitration)` (next to `.AddSingleton(settings.WyomingClient)`) and `.AddSingleton<WakeArbiter>()` (next to `ActiveAlertRegistry`).

- [ ] **Step 1: Write the failing test** — append to `ConfigModuleTests.cs` (mirror its existing resolution-test style):

```csharp
    [Fact]
    public void ConfigureVoiceChannel_ResolvesWakeArbiter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(new VoiceSettings());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WakeArbiter>().ShouldNotBeNull();
        provider.GetRequiredService<ArbitrationSettings>().WindowMs.ShouldBe(500);
    }
```

(Adapt the setup lines to whatever `ConfigModuleTests.cs` already does to satisfy `ConfigureVoiceChannel`'s dependencies — read the file first; if existing tests stub Redis or skip resolution of hosted services, follow the same pattern.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfigModuleTests"`
Expected: FAIL — `WakeArbiter` not registered.

- [ ] **Step 3: Implement** items 1–5 above. The host ctor param order: insert `WakeArbiter arbiter` after `TimeProvider time,` and before `ILogger<WyomingSatelliteHost> logger` — DI fills it positionally regardless; keep the optional `speakerVerifier` last.

- [ ] **Step 4: Build + run the touched test surfaces**

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfigModuleTests|FullyQualifiedName~WakeArbiterTests|FullyQualifiedName~FollowUpConversationTests|FullyQualifiedName~SatelliteSession"`
Expected: build clean, all PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/ConfigModuleTests.cs
git commit -m "feat(voice): wire WakeArbiter into the satellite host and DI"
```

---

### Task 12: Two-fake-satellite end-to-end test

**Files:**
- Create: `Tests/Unit/McpChannelVoice/Wyoming/FakeSatelliteServer.cs`
- Create: `Tests/Unit/McpChannelVoice/WakeArbitrationHostTests.cs`

**Interfaces:**
- Consumes: the full Task 11 wiring; `WyomingReader`/`WyomingWriter` (public, `McpChannelVoice.Services.WyomingProtocol`); the `IConversationFactory` stub from `VoiceConversationManagerTests.cs`; `CapturingEmitter` (existing test helper) for `TranscriptDispatcher`.
- Produces: proof on real sockets that (a) two coincident wakes yield exactly ONE dispatched `channel/message` and the quieter satellite receives `pause-satellite`, and (b) the loser plays no done-cue path (no `transcript` to the loser).

`FakeSatelliteServer` contract (in-process Wyoming server, mirroring the real satellite's wire behavior):

```csharp
// Minimal in-process Wyoming satellite: accepts ONE hub connection, records every event the hub
// sends, and lets the test push satellite->hub events. Mirrors nabu-satellite's wire behavior
// only as far as arbitration needs: run-pipeline with data, audio chunks, audio-stop on pause.
public sealed class FakeSatelliteServer : IAsyncDisposable
{
    // Bind 127.0.0.1:0, expose Port; AcceptAsync() awaits the hub dialing in.
    // SendAsync(WyomingEvent) writes with WyomingWriter; ReceivedEvents is a thread-safe
    // snapshot of everything read with WyomingReader on a background pump.
    // WaitForEventAsync(string type, TimeSpan timeout) polls ReceivedEvents for the hub->satellite
    // event the assertion needs (pause-satellite / transcript / run-satellite).
}
```

Test skeleton (`WakeArbitrationHostTests.cs`) — construct the host exactly as `ConfigModule` does but with stubs, then drive both fakes:

```csharp
public class WakeArbitrationHostTests
{
    private sealed class FixedSpeechToText : ISpeechToText
    {
        // return a fixed "hola" transcription result; copy the result-shape construction from
        // an existing ISpeechToText stub in Tests/Unit/McpChannelVoice/Stt if one exists.
    }

    [Fact]
    public async Task CoincidentWakes_OneDispatch_LoserGetsPauseSatellite()
    {
        await using var satA = new FakeSatelliteServer();
        await using var satB = new FakeSatelliteServer();

        var voiceSettings = new VoiceSettings
        {
            Satellites = new Dictionary<string, SatelliteConfig>
            {
                ["a"] = new() { Identity = "household", Room = "A", Address = $"tcp://127.0.0.1:{satA.Port}" },
                ["b"] = new() { Identity = "household", Room = "B", Address = $"tcp://127.0.0.1:{satB.Port}" }
            },
            WyomingClient = new WyomingClientSettings
            {
                TrailingSilenceMs = 200, MinSpeechMs = 80 // fast endpoint for the test
            },
            FollowUp = new FollowUpSettings { Enabled = true, ReplyTimeoutMs = 500 },
            Arbitration = new ArbitrationSettings { WindowMs = 300 }
        };

        // registries/manager/dispatcher: same construction as ConfigModule, with the
        // IConversationFactory stub from VoiceConversationManagerTests and CapturingEmitter.
        // host: new WyomingSatelliteHost(voiceSettings.WyomingClient, voiceSettings, ...,
        //     new FixedSpeechToText(), dispatcher, alerts, metrics, TimeProvider.System,
        //     arbiter, NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await satA.AcceptAsync();
        await satB.AcceptAsync();
        await satA.WaitForEventAsync("run-satellite", TimeSpan.FromSeconds(5));
        await satB.WaitForEventAsync("run-satellite", TimeSpan.FromSeconds(5));

        // Both wake within the window; A is closer (louder wake word).
        await satA.SendAsync(RunPipeline(wakeRms: 900));
        await satB.SendAsync(RunPipeline(wakeRms: 200));
        // Both stream the same short utterance: 3 loud chunks (240 ms speech) + 4 silent.
        foreach (var sat in new[] { satA, satB })
        {
            for (var i = 0; i < 3; i++) { await sat.SendAsync(AudioChunkEvent(3000)); }
            for (var i = 0; i < 4; i++) { await sat.SendAsync(AudioChunkEvent(0)); }
        }

        // Loser: pause-satellite, never a transcript. Winner: transcript after its turn ends
        // (dispatch happened, no reply arrives, 500 ms reply timeout ends the conversation).
        await satB.WaitForEventAsync("pause-satellite", TimeSpan.FromSeconds(5));
        await satA.WaitForEventAsync("transcript", TimeSpan.FromSeconds(5));
        satB.ReceivedEvents.Count(e => e.Type == "transcript").ShouldBe(0);

        emitter.Messages.Count.ShouldBe(1);          // exactly one channel/message left the hub
        emitter.Messages[0].SatelliteId.ShouldBe("a");

        await host.StopAsync(CancellationToken.None);
    }

    private static WyomingEvent RunPipeline(double wakeRms) =>
        WyomingEvent.Header("run-pipeline", new JsonObject
        {
            ["source"] = "wake", ["wake_rms"] = wakeRms, ["wake_score"] = 0.9
        });

    private static WyomingEvent AudioChunkEvent(short amplitude)
    {
        var bytes = new byte[2560];
        foreach (var i in Enumerable.Range(0, 1280))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return WyomingEvent.WithPayload("audio-chunk",
            new JsonObject { ["rate"] = 16000, ["width"] = 2, ["channels"] = 1 }, bytes);
    }
}
```

- [ ] **Step 1: Write `FakeSatelliteServer` + the test.** Read `Tests/Unit/McpChannelVoice/CapturingEmitter.cs`, `TranscriptDispatcherTests.cs`, and `VoiceConversationManagerTests.cs` FIRST and reuse their construction patterns verbatim for the emitter, dispatcher, and conversation-factory stub. Real `TimeProvider.System` + short windows (300/500 ms) — no FakeTimeProvider here, this test exercises real socket timing.

- [ ] **Step 1b: Add the wire-level steal scenario** as a second test in the same class — the spec's Rule B over real sockets:

```csharp
    [Fact]
    public async Task WakeDuringAnotherSatellitesOpenCapture_MuchLouder_StealsTheTurn()
    {
        // Same harness as above. Choreography:
        // 1. satA wakes alone (rms 600) and streams 3 loud chunks — then KEEPS the capture open
        //    (no trailing silence yet): A is now the holder with an open capture whose history
        //    shows a speech onset.
        // 2. ~1s later satB wakes (rms 20000 — well past the 6 dB steal margin over what A heard)
        //    and streams the same short utterance to completion.
        // 3. Assert: satA receives pause-satellite (aborted holder, no transcript to A with text);
        //    satB is NOT paused and the single dispatched message carries SatelliteId == "b".
        // Note: A never dispatched (its capture never endpointed), so there is no conversation to
        // transfer — TransferBinding's id-survival contract is pinned by the WakeArbiterTests
        // handoff unit test instead; this test pins the wire mechanics of the steal.
    }
```

Write the body following the first test's helper calls (`RunPipeline`, `AudioChunkEvent`, `WaitForEventAsync`); the timing constraint that matters is that satB's claim arrives while satA's capture is still open (send satA no silent chunks until after satB's window closes).

- [ ] **Step 2: Run to verify the red state** (before Task 11 pieces existed this would fail; now the test should be written to pass — if it fails, the failure IS the bug report; debug the wiring, not the test):

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WakeArbitrationHostTests"`
Expected: PASS (this is the integration proof for Tasks 1–11's hub half; a failure means a real wiring bug).

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/McpChannelVoice/Wyoming/FakeSatelliteServer.cs Tests/Unit/McpChannelVoice/WakeArbitrationHostTests.cs
git commit -m "test(voice): end-to-end wake arbitration over fake Wyoming satellites"
```

---

### Task 13: Docs + full verification sweep

**Files:**
- Modify: `satellite/CLAUDE.md` (protocol invariants)
- Modify: `CLAUDE.md` (Voice Satellite Architecture paragraph)
- Modify: `docs/superpowers/specs/2026-07-27-wake-arbitration-design.md` (Status → Implemented)

- [ ] **Step 1: Update `satellite/CLAUDE.md`** — in the Invariants section, extend the wire-format bullet area with:

```markdown
- **Wake metadata & arbitration**: `run-pipeline` carries `{"source":"wake"|"button","wake_rms":f32,"wake_score":f32}` (rms measured over the pre-roll ring minus the detection gap, BEFORE trim — i16-amplitude units matching the hub's SilenceGate). The hub may reply `pause-satellite` (arbitration loss): Streaming → audio-stop back, Idle, detector reset, NO cue, LED Idle; Idle → no-op. PROTOCOL_VERSION is 1.3.
```

- [ ] **Step 2: Update root `CLAUDE.md`** — append one sentence to the Voice Satellite Architecture section, after the sentence about `transcript` re-arming wake:

```markdown
When several satellites hear the same wake word, the hub's `WakeArbiter` picks one winner (calibrated `wake_rms`, 500 ms coincidence window, onset-alignment check against open captures) and silently re-arms the losers via `pause-satellite`; a much-louder wake during another satellite's open conversation hands the conversation off to the winner (see `docs/superpowers/specs/2026-07-27-wake-arbitration-design.md`).
```

- [ ] **Step 3: Flip the spec status line** to `**Status:** Implemented (see docs/superpowers/plans/2026-07-27-wake-arbitration.md)`.

- [ ] **Step 4: Full verification sweep** (evidence before claims — run all three, read the output):

```bash
cd satellite && cargo test && cd ..
dotnet build agent.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"
```

Expected: cargo suite green; solution builds; unit suite green (the pre-existing McpAgent cleanup-test failure is a known baseline — judge by failure TYPE, not count).

- [ ] **Step 5: Commit**

```bash
git add satellite/CLAUDE.md CLAUDE.md docs/superpowers/specs/2026-07-27-wake-arbitration-design.md
git commit -m "docs: wake arbitration protocol invariants and architecture notes"
```

---

## Deployment (manual, after merge — not part of this plan's execution)

1. `satellite/scripts/build-release.sh` + `scripts/provision-satellite-rs.sh` per Pi (satellite first — new fields are harmless to the old hub).
2. Rebuild/redeploy `mcp-channel-voice`.
3. Watch `WakeTriggered.WakeRms` across rooms for a few days; set `Satellites__<id>__RmsOffsetDb` if the units differ; verify the XVF3800-AGC caveat (spec §8). `Arbitration__Enabled=false` is the kill switch.
