# Adaptive End-of-Utterance Detection (TV-in-Background Fix)

**Date:** 2026-07-20 · **Branch:** `noise` · **Status:** Approved

## Problem

With a TV (or any speech-like background) playing in the room, a wake-triggered
capture never ends: the user says "ok nabu", speaks a command, stops — and the
satellite keeps streaming TV audio. The turn only closes at the `MaxUtteranceMs`
cap (300 s in prod config), producing a minutes-long capture and one giant
transcript with the command buried in TV dialog. Reproduced on the prod
fran-office satellite (XVF3800 mic, firmware AGC).

**Root cause:** `SilenceGate` classifies a chunk as speech with a fixed absolute
RMS test (`rms >= 700`). TV dialog keeps the level above that bar indefinitely,
so trailing silence never accumulates. Downstream guards cannot help: the STT
gibberish gate (`no_speech_prob`/`avg_logprob`) passes TV dialog because it *is*
real speech, and `SegmentedSpeechToText`'s phrase slicer uses a second fixed
threshold (500) that also never fires, so the capture decodes as one giant
segment at the end.

## Research summary

Surveyed classic endpointers (Rabiner–Sambur, G.729B/AMR, `speech_recognition`),
neural VADs (Silero, WebRTC, TEN), ASR/semantic turn detection (Kaldi endpoint
rules, smart-turn, LiveKit), and Home Assistant's pipeline (which has this exact
bug, closed "not planned").

- **What works:** replacing the absolute reference with *measured* references.
  A single far-field mic offers exactly two: the **noise floor** and the
  **running utterance peak**. Robust endpointers use both, with hysteresis
  (enter high / exit low) and a trailing-silence hangover.
- **What does not:** neural VADs (TV dialog is speech — they inherit the bug),
  noise suppression (targets stationary noise; TV speech is non-stationary),
  AEC (no reference signal for a TV), semantic turn detection (gated on a VAD
  going low, which under TV audio never happens).

## Goals

1. Turns end promptly after the user stops speaking with a TV in the room.
2. Follow-up windows stop capturing TV dialog as user turns.
3. Quiet-room behavior is unchanged (zero regression).
4. Robust under the XVF3800 firmware AGC and under the satellite's music
   duck/unduck level steps.
5. Generic signal-level fix — no model-specific post-processing, no transcript
   dropping, no new runtime dependencies.

**Non-goals:** speaker identification / device-directed-speech modeling, mic-array
spatial methods, satellite (Rust) or Wyoming protocol changes, HA "mute the TV"
automations (possible later as an orthogonal complement).

## Design

### AdaptiveLevelTracker (new, `McpChannelVoice/Services/WyomingProtocol/`)

Pure, allocation-light (fixed ring buffer), works in dB. Consulted per audio
chunk (~80 ms) by `SilenceGate` in place of the absolute comparison.

- **Noise floor = windowed minimum** (lightly EMA-smoothed) of per-chunk RMS
  (dB) over the
  trailing `FloorWindowMs` (default 3000 ms). Falls instantly when the room gets
  quieter (duck engage, TV going quiet); rises only as loud frames age out, so
  the user's own speech cannot drag it up (word gaps and breaths re-seed the true
  background); recovers from an upward step (duck force-restore, TV scene change)
  within one window length.
- **Speech criterion, floor-relative with hysteresis:** a frame *enters* speech
  at level ≥ `max(clamp, floor + EnterMarginDb)` (default +9 dB) and *stays*
  speech until it drops below `max(clamp, floor + ExitMarginDb)` (default
  +4 dB) — the same clamp on both sides. The clamp is the existing
  `SilenceRmsThreshold` (700), meaning unchanged: in a quiet room both
  thresholds collapse to the clamp, reproducing today's single-threshold
  behavior exactly; the adaptive term only ever raises the bar when the room is
  loud. dB ratios survive AGC gain shifts (floor and speech move together).
- **Peak-relative backstop:** once real speech has been observed, frames more
  than `PeakDropDb` (default 15 dB) below the running utterance peak count as
  silence even if the floor estimate is off. Near-field user speech sits
  ~15–25 dB above a TV at the mic; when the user stops, the level falls off that
  cliff regardless of the floor tracker's state.

### SilenceGate (modified)

State machine unchanged (min-speech, trailing-silence accumulation, no-speech
timeout, max-utterance cap, peak tracking). Only the per-chunk speech/silence
classification is delegated to the tracker. End of utterance: after
`MinSpeechMs` of speech, `TrailingSilenceMs` (2000 ms, unchanged) of consecutive
non-speech ends the turn. `MaxUtteranceMs` drops **300 s → 40 s** and becomes a
pure runaway backstop; it is per-capture, so consecutive/follow-up turns each
get a fresh 40 s.

Follow-up windows are fixed by the same mechanism: TV-only audio never crosses
`floor + EnterMarginDb` (the floor *is* the TV), so `NoSpeechTimeout` fires and
the conversation closes instead of dispatching TV dialog.

### SegmentedSpeechToText (modified)

Its internal phrase-slicing gate gets its own `AdaptiveLevelTracker` instance;
its existing `SilenceRmsThreshold` (500) likewise becomes the clamp. Margins and
window come from the same settings record — one tuning surface.

### Music ducking interaction (satellite untouched)

The satellite ducks music on Listening/Speaking and restores on Thinking/Idle
(debounced), with a `MAX_DUCK` 30 s safety restore.

- Duck engage at capture start: downward step — the windowed min absorbs it
  instantly. The ~240 ms pre-roll carrying pre-duck-level music ages out of the
  window in 3 s and cannot anchor the floor.
- `MAX_DUCK` force-restore mid-capture: upward step — the floor re-converges
  within one window (~3 s); during convergence un-ducked music may read as
  speech, so a pathological >30 s turn ends at the 40 s cap instead of ~32 s.
  Normal turns end long before 30 s; no satellite change warranted.

### Configuration

`WyomingClientSettings` (code defaults; `appsettings.json` overrides):

| Setting | Default | Notes |
|---|---|---|
| `FloorWindowMs` | 3000 | floor tracker window |
| `EnterMarginDb` | 9 | speech entry margin over floor |
| `ExitMarginDb` | 4 | speech exit margin (hysteresis) |
| `PeakDropDb` | 15 | silence when this far below utterance peak |
| `SilenceRmsThreshold` | 700 (500 streaming) | **kept** — reinterpreted as quiet-room clamp, no migration |
| `MaxUtteranceMs` | 300000 → **40000** | runaway backstop only |

Per-satellite `GateSettings` grows the same four optional overrides with the
existing `Resolve*` pattern. No new env vars or secrets. **Rollout note:** the
pi5 prod compose `.env` may pin `WyomingClient__*` values that shadow new
appsettings defaults — check at deploy time.

### Metrics

`CaptureStats` grows `FloorRms` and `EndReason`
(`TrailingSilence`/`MaxUtterance`/`NoSpeech`/`Forced`) alongside
`PeakRms`/`SpeechMs`, flowing through the existing `TranscriptDispatcher` →
`UtteranceTranscribed` path so margins can be tuned from dashboard data.

### Testing (TDD, red-green-refactor)

Unit tests drive the tracker and gate with synthetic PCM frames:

- Quiet room: clamp dominates; behavior identical to today (regression pin).
- Constant babble floor: speech-over-TV ends on trailing silence; TV-only
  follow-up hits `NoSpeech`.
- Duck step-down and restore step-up: floor convergence within one window.
- Rising-gain ramp (AGC): relative margins hold.
- Drifted floor: peak-drop backstop still ends the turn.
- 40 s runaway cap; per-capture cap freshness across consecutive turns.
- `SegmentedSpeechToText`: phrase slicing under babble background.

All pure DSP — no Docker services needed.

## Known limit

If the TV is as loud as the user *at the mic*, no single-mic energy method can
separate them; the turn then ends via the peak backstop or the 40 s cap. Remedies
beyond this design (speaker-conditioned VAD, mic-array DRR) are explicitly out of
scope.

## Refinements during planning (2026-07-20)

1. Floor smoothing: pure windowed minimum, no EMA — the min already provides
   instant-fall / window-delayed-rise; an EMA adds a constant without behavior.
2. The floor seeds from the first real chunks — no clamp-only grace period. A
   grace period would let above-clamp TV count as speech, latch minSpeech, and
   end the turn via trailing silence before the user speaks. Consequences:
   TV-only follow-up windows correctly time out as no_speech; on a TV-background
   wake turn the user must start speaking within the no-speech window (5 s), as
   today; a capture opening mid-loud-speech with zero leading gap reads that
   speech as floor until a pause of roughly the smoothing window re-seeds the
   minimum (superseded by refinement 5: sub-smoothing-window dips average away
   by design; leading quiet still seeds the floor instantly because a partial
   smoothing window contains only the quiet frames — the satellite pre-roll's
   detection-latency gap supplies exactly that).
3. Peak backstop armed only in the adaptive regime (floor + EnterMarginDb >
   clamp) so quiet-room loud-then-soft speech can never be clipped (Goal 3).
4. The windowed-min floor climbs to speech level when fed constant-amplitude
   audio with no dips for longer than the floor window (surfaced by the Task 4
   pin test). Real speech re-seeds the floor via intra-word dips; synthetic
   tests (and sustained tones like humming) must keep the floor window longer
   than their longest dip-free run — the pin test uses 800 ms for its 600 ms
   runs. Production keeps FloorWindowMs 3000, well above natural dip spacing.

5. Field fix (2026-07-20, pi5): the floor min-window is fed duration-weighted
   SMOOTHED energy (500 ms moving average) instead of raw chunk levels. Real TV
   dialog pauses between phrases for 100-400 ms; the raw minimum latched onto
   those lulls (measured FloorRms 72-97 = room silence with the TV on), leaving
   the gate in clamp mode. Smoothing makes the floor ride the TV's speaking
   level while sustained silence still lowers it within ~one smoothing window.

6. Field fix (2026-07-20): captures with NO user speech under TV. The claim
   "TV-only audio never crosses floor + EnterMarginDb" only holds for a
   *converged* floor; each capture starts a fresh tracker, and one that opens
   during a TV lull (inter-phrase gap, scene transition, the pre-roll gap)
   seeds the floor at near-silence. Resumed TV then reads as speech until the
   min-window turns over (≤ FloorWindowMs), latching minSpeech — which
   permanently disables the NoSpeech outcome — so the capture ended as a
   dispatchable utterance full of TV dialog. Fix: when a capture is about to
   end via trailing silence, the gate demotes it to NoSpeech unless the
   speech-classified peak stands ≥ the demote margin above the converged
   floor (`AdaptiveLevelTracker.SpeechProminent`). Convergence pseudo-speech
   sits AT the converged floor; real speech that latched against a converged
   floor clears the default margin by construction. Applied only when a
   no-speech window is configured, so the segmenting gate inside
   SegmentedSpeechToText keeps slicing on EndUtterance. A continuous
   annulment variant (revoke speech credit whenever the floor rises to explain
   the peak) was rejected: the floor is fed the utterance's own speech energy,
   so a long dense utterance could annul genuine speech mid-command.
   Residual (accepted): TV reaching the 40 s cap or a genuinely prominent
   burst (music sting well above the converged floor) still dispatches —
   energy alone cannot separate those from real speech (see Known limit); the
   STT confidence gate remains the downstream defense.

7. Field calibration (2026-07-20 night session, fran-office/XVF3800, prod
   transcripts + metrics): the demote's reference was switched from the
   trailing-run MEAN to the converged FLOOR (windowed min), and its margin
   decoupled into `DemoteMarginDb` (global + per-satellite `Gate` override;
   null inherits EnterMarginDb; appsettings ships 10). Why: the trailing mean
   sits 2-4 dB above the floor min, and a real command was measured at only
   11.2 dB over a loud-TV floor (AGC compresses near-field advantage), i.e.
   inside that gap — trailing+9 would have demoted genuine speech. Measured
   bracket for tuning: TV leak turns ran 14.4-18.9 dB over floor with STT
   confidence up to 0.62 (a clean TV monologue out-scored a real command's
   0.42 — confidence thresholds CANNOT separate TV), while the softest real
   command sat at 11.2 dB. Any DemoteMarginDb above ~11 trades TV rejection
   directly for dropped soft commands; the leaks above that line are the
   Known limit. TrailingRms plus rejection-side capture stats (published on
   FollowUpTimedOut) exist to audit both directions of the trade from the
   dashboard. Remaining structural failure observed: the agent's reply
   re-opens a follow-up window beside a talking TV, so one leak can chain
   into a TV↔agent loop — out of scope here; needs agent-signaled
   conversation close (turn-identity reply protocol, deferred).
