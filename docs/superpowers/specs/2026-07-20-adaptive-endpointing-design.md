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
