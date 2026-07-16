# Voice Gibberish Protection — Design

**Date:** 2026-07-17
**Status:** Approved

## Problem

The satellite occasionally wakes on background noise (accepted — wake tuning is out of scope). When that noise is loud enough to pass the hub's speech gate, it reaches STT, whisper hallucinates a gibberish transcript, and the agent processes it as a real request. Two defenses are missing:

1. A working low-confidence transcript gate. The gate *exists* (`TranscriptDispatcher` drops `Confidence < ConfidenceThreshold`, default 0.4) but is dead code: the Wyoming protocol's `transcript` event carries no confidence field (verified against `OHF-Voice/wyoming` v1.10.0 source and full changelog), and `wyoming-faster-whisper` (through 3.5.0) discards every quality signal faster-whisper produces — its internal `Transcriber` contract returns a bare `str`. `WyomingSpeechToText` parses a non-standard `score` field that no stock server emits, so `Confidence` is always null, and null passes the gate.
2. A higher bar for what audio counts as speech worth transcribing. Today chunk RMS ≥ 500 starts speech and 200 ms of cumulative speech is enough to send the whole capture to STT.

## Decisions made

- **Confidence source:** patched wyoming-whisper image (chosen over speaches/HTTP swap and over audio-gates-only). Accuracy/latency identical by construction; smallest change; fails open.
- **Rejection UX:** nothing new — a dropped transcript ends the turn exactly like today (done-cue + re-arm), on wake turns and mid-follow-up alike. Drops are observable only via metrics.
- **Standing constraint honored:** no text-based gibberish heuristics or model-specific post-processing; the gate must be conservative — dropping real speech is worse than passing occasional gibberish.

## 1. Patched wyoming-whisper

New build context `DockerCompose/wyoming-whisper/`:

- `Dockerfile` — `FROM rhasspy/wyoming-whisper@sha256:…` pinned by digest to the 3.5.0 release (digest resolved from the registry at implementation time) + overlay of patched files. Compose service `wyoming-whisper` switches from `image:` to `build:`; flags unchanged.
- Patch to `faster_whisper_handler.py`: materialize the segments returned by `model.transcribe(...)`; compute utterance aggregates — duration-weighted mean `avg_logprob`, duration-weighted mean `no_speech_prob`, max `compression_ratio` — and carry them alongside the joined text.
- Patch to `dispatch_handler.py`: where the transcript event is emitted, attach extra JSON keys: `score`, `avg_logprob`, `no_speech_prob`, `compression_ratio`. Extra keys on Wyoming events are backward-compatible (canonical clients read only `text`).

**Score formula (server-side, deliberately dumb):** `score = exp(weighted_avg_logprob)` → (0,1], ≈ mean token probability. All recalibration intelligence lives hub-side; the raw stats ride along so formula evolution never requires an image rebuild.

**Threshold:** keep `ConfidenceThreshold = 0.4`. On this scale 0.4 ≈ `avg_logprob −0.92`, close to whisper's canonical junk threshold (`logprob_threshold = −1.0`). Recalibrate later from dashboard data.

**Fail-open invariant:** if the event carries no `score` (stock image, patch regression), `Confidence` is null and the gate passes everything — i.e. exactly today's behavior. A broken patch can never kill the voice pipeline.

## 2. Hub-side changes

- `WyomingSpeechToText`: parse the three raw stats into new optional `TranscriptionResult` fields (metrics only, no gating on them in v1). Harden the `score` parse with the tolerant `WyomingNumber` helper instead of bare `GetValue<double>()` (a non-numeric value must not surface as an STT failure).
- `SegmentedSpeechToText`: aggregate per-segment confidences duration-weighted (segment audio lengths are known) instead of the current unweighted mean; same for the raw stats.
- `TranscriptDispatcher`: unchanged gating logic (whole-utterance drop). No per-segment transcript filtering in v1 — too easy to eat real phrases.
- Voice-approval capture (`RequestApprovalTool`) stays confidence-blind: its yes/no grammar already degrades gibberish to "rejected", which is safe.

## 3. STT-entry bar

Config bumps in `McpChannelVoice/appsettings.json` (values remain knobs, env-overridable):

- `WyomingClient.SilenceRmsThreshold` **500 → 700** (≈ −36 → −33 dBFS on S16LE). Sub-threshold wakes fall into `NoSpeech`, which skips STT entirely.
- `WyomingClient.MinSpeechMs` **200 → 300**. Transients shorter than 300 ms cumulative land in `NoSpeech`. Deliberately below short Spanish follow-ups ("sí", "para" ≈ 350–500 ms); do not raise blind.
- Inner streaming gate (`Stt.Streaming.*`) untouched — it only slices phrases inside already-accepted speech.
- **New per-satellite overrides** for these two values on `SatelliteConfig` (nullable; fall back to global). The `SilenceGate` is constructed per-turn from session config, so wiring is local to `WyomingSatelliteHost`. Rationale: XVF3800 firmware AGC raises noise-floor levels relative to other hardware; one global RMS number won't fit all satellites.

## 4. Observability

- Extend the `UtteranceTranscribed` voice metric (both `dispatched` and `dropped` outcomes) with: `avg_logprob`, `no_speech_prob`, `compression_ratio`, peak RMS, and cumulative speech ms of the capture.
- Ensure `NoSpeech` outcomes are visible as a metric (add if missing) so the effect of the raised entry bar is measurable.
- All new `VoiceMetric`/`VoiceDimension` enum members get explicitly pinned values (int-serialization corruption precedent).
- Outcome: dashboard shows score distributions for real speech vs. noise; threshold recalibration = reading a histogram.

## 5. Testing & verification

TDD (Red-Green-Refactor) hub-side:

- `WyomingSpeechToText`: score/stat parsing — present, absent (null Confidence), non-numeric (tolerated, not an STT failure).
- `SegmentedSpeechToText`: duration-weighted aggregation across segments.
- `TranscriptDispatcher`: drop/pass on either side of the threshold; null passes.
- Per-satellite gate override wiring; metric field emission.

Server patch verification: build the image; scripted Wyoming round-trip (clean speech WAV → high score; noise WAV → low score; raw keys present); fail-open check against the stock image (no `score` → null Confidence). Final on-device sanity pass on fran-office.

## Out of scope (YAGNI)

Text-based gibberish heuristics, per-segment transcript filtering, retry-with-bigger-model, satellite-side changes (wake thresholds, VAD), rejection earcons/spoken retry, gating the approval-capture path.
