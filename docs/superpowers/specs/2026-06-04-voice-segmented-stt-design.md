# Voice Segmented STT (Decode-Ahead) — Design Spec

**Date**: 2026-06-04
**Status**: Approved for planning
**Owner**: Francisco Crespo

## Goal

Reduce the latency between a speaker finishing an utterance and the agent receiving the transcript, **without reducing transcription accuracy**, by decoding speech *while the user is still talking* instead of only after end-of-speech.

The win is delivered to the rest of the pipeline for free: once `ISpeechToText` finalizes sooner, `TranscriptDispatcher.DispatchAsync` hands the transcript to the LLM sooner. We keep a *finalize-fast* model — only the single, complete final transcript is delivered to the agent. Partial hypotheses are **never** surfaced to the LLM.

## Background — current state

After the local wake word fires, `wyoming-satellite` streams mic audio open-endedly to the hub. The hub (`WyomingSatelliteHost.RunConnectionAsync`) feeds each `audio-chunk` through a `SilenceGate` (`McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`) that decides end-of-utterance from a run of trailing silence, writes chunks to an unbounded `Channel<AudioChunk>`, and runs `TranscribeAndReplyAsync` concurrently from utterance start. That task calls `ISpeechToText.TranscribeAsync(IAsyncEnumerable<AudioChunk>, …)`.

The default backend is **local Whisper**: `WyomingSpeechToText` streams chunks live to a stock `rhasspy/wyoming-whisper` container (`McpChannelVoice/Services/Stt/WyomingSpeechToText.cs`; compose service `wyoming-whisper`, model `small`, `--device cpu`, `int8_float32`, beam-size 5, Spanish). Audio is uploaded incrementally, but `faster-whisper` is a **batch** model: it only returns a transcript after `audio-stop`. So post-speech latency ≈ the trailing-silence confirmation window (`TrailingSilenceMs`, default **800 ms**) **+** a full-utterance Whisper decode that starts only *after* the user stops.

## Constraints and choices

- **Accuracy is mandatory — keep or improve, never regress.** This is the hard constraint that shapes every decision below. It is enforced by an offline WER measurement gate (see *Accuracy guards*).
- **Local Whisper is the priority backend.** Design works with the stock `rhasspy/wyoming-whisper` container — no new STT server, no new protocol, no GPU dependency.
- **Compute varies (CPU acceptable, GPU exploited when present).** The design must stay usable on CPU and degrade gracefully toward today's batch behavior when the machine is slow.
- **Finalize-fast, not live partials.** No incremental text to the LLM, no barge-in in this scope.
- **No model downgrade.** Speed comes from *overlapping inference with speech*, never from a cheaper/less-accurate model or smaller beam.

## Approach — VAD-segmented decode-ahead (chosen)

Split the utterance at **natural speech pauses** (VAD boundaries), decode each completed phrase-segment in the background as soon as its trailing pause is detected, and **concatenate** the segment transcripts in order to form the final result.

### Why this is the accuracy-safe way to beat the 800 ms floor

The only prefix of the audio that equals the whole utterance is the one at the true end of speech. Any decode started earlier is a decode of a *partial*, and Whisper's output for a partial is not the prefix of its output for the whole (`"turn on the"` ≠ a prefix of `"turn on the lights"`). Therefore saving more than the trailing-silence window inherently means decoding partials, and the question becomes *how* to partition so the partials stay accurate:

- **Cutting on the clock (fixed intervals + overlap + stitch)** splits words mid-syllable, triggers Whisper hallucination on short/partial chunks, and forces a *fuzzy* heuristic merge of overlap regions that transcribe differently in each chunk. This degrades accuracy — **rejected**.
- **Cutting on VAD pauses** splits only where the speaker naturally pauses between phrases. Segments are clean linguistic units and **disjoint in time**, so the merge is exact **concatenation**, not fuzzy stitching. This is the chosen approach.

### Cost profile — why it fits "compute varies"

Because segments are disjoint in time, **every audio frame is decoded exactly once across all segment decodes combined**. Total Whisper compute ≈ a single whole-utterance batch decode (O(n)), merely sliced into pieces and time-shifted earlier to overlap with speech. This is the key difference from a rolling-window approach (which re-decodes growing prefixes → O(n²) and needs a GPU). On a slow CPU, A+ does the same total work as today, just sooner; if speech outpaces the decoder, segment decodes queue and the result degrades gracefully toward batch latency. On a GPU the overlap is near-total.

### Rejected alternatives

- **Single end-of-speech speculative decode ("safe 800 ms")** — provably identical accuracy but caps the saving at the trailing-silence window. A+ subsumes it: an utterance with no internal pauses *is* a single segment and behaves exactly this way.
- **Rolling-window continuous re-decode** — O(n²), GPU-bound, and tempts use of unstable partials.
- **Streaming-native server (WhisperLive / whisper_streaming)** — new container + websocket protocol, GPU-oriented, LocalAgreement stabilization trades accuracy for incrementality. Violates local/CPU/simple constraints.

## Architecture

A new **`SegmentedSpeechToText` decorator** implements `ISpeechToText` and wraps the configured inner backend (`WyomingSpeechToText` by default). The public contract is **unchanged** — `Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk>, …)` — so the hub wiring, the OpenAI/OpenRouter backends, and `TranscriptDispatcher` are untouched. All segmentation is internal to the decorator.

```
satellite audio-chunks ──▶ Hub SilenceGate (end-of-utterance, 800 ms) ──▶ utterance Channel<AudioChunk>
                                                                              │
                                          SegmentedSpeechToText.TranscribeAsync(stream)
                                          │  internal SilenceGate tuned for phrase pauses (≈350 ms)
   [phrase1]<pause>                       ├─▶ segment close ▶ snapshot buffer ▶ background decode via inner STT
   [phrase2]<pause>                       │      (SemaphoreSlim backpressure: ≤N in-flight, N=1 on CPU)
   ...                                    │   results held by segment index
   [phraseN]  …stream ends (800 ms) ──────┴─▶ close final segment ▶ await outstanding decodes ▶ concat ▶ result
```

### Segmentation via reused `SilenceGate`

The decorator runs its **own** `SilenceGate` instance, configured with the *segment* trailing-silence threshold (≈350 ms) and a *min-segment* speech floor (≈800 ms). Each `Decision.EndUtterance` from this inner gate marks a **phrase boundary**: the decorator snapshots the accumulated audio as a segment, calls `inner.TranscribeAsync` on it in the background, then `Reset()`s the gate and continues buffering into the next segment.

Two consequences fall out of `SilenceGate`'s existing semantics and require no new logic:

- **Merge-forward of short fragments is automatic.** `SilenceGate` only returns `EndUtterance` once `_speechElapsed > minSpeech`. A burst shorter than the min-segment floor never closes a segment; its audio keeps accumulating into the same segment until enough speech is seen. This avoids decoding sub-second clips alone (Whisper's hallucination zone).
- **Trailing silence after the last phrase is dropped.** The hub completes the channel ~800 ms into the final silence; the decorator's gate already closed the final *speech* segment ~350 ms in, so the remaining audio is pure silence with no speech → not a segment → discarded.

Because the decorator's threshold (≈350 ms) is shorter than the hub's (800 ms), the final phrase's decode starts ~450 ms **before** the channel completes — delivering the end-of-speech overlap even for single-phrase utterances.

### Finalization & concatenation

On stream end the decorator closes any open speech segment, awaits all outstanding segment decodes, and concatenates their `Text` in segment order with whitespace/punctuation normalization (`string.Join(" ", …)` + trim/collapse). `Language` is taken from the first segment that reports one; `Confidence` is null (or the mean when all segments report one). The result is returned as a single `TranscriptionResult`, indistinguishable to the caller from a batch transcript.

### DI wiring

In `McpChannelVoice/Modules/ConfigModule.cs`, after the inner `ISpeechToText` is constructed (Wyoming/OpenAI/OpenRouter as today), wrap it in `SegmentedSpeechToText` **when `Stt:Streaming:Enabled` is true**. When disabled, the inner backend is registered directly — exact current behavior, zero risk. The decorator is backend-agnostic, so it also accelerates cloud backends, but local Whisper is the validated target.

## Accuracy guards (enforcement of the mandate)

- **Min-segment floor (~800 ms speech):** never decode a sub-second fragment alone; merge forward (automatic via `SilenceGate.minSpeech`). A too-short **final** segment merges **backward** — its audio is appended to the previous segment and that segment is re-decoded, replacing the previous result (bounded: one extra decode).
- **Tunable thresholds are the accuracy/speed knob.** Raising the segment-pause and min-segment thresholds yields fewer, longer, more context-complete segments (closer to batch accuracy, less overlap); lowering them yields more overlap (more speed, more context-loss risk). All config, no code change.
- **Offline WER measurement gate (acceptance criterion):** an integration test runs a corpus of recorded Spanish utterances (WAV + reference transcript) through both the plain inner backend (whole-utterance) and `SegmentedSpeechToText`, computes WER against the reference for each, and asserts `segmented_WER ≤ batch_WER + ε`. A+ ships only if this passes. The harness reads fixtures from a corpus directory; absent a corpus it skips (mirroring the existing live-site test pattern), but the gate must be run against a real seed corpus before enabling in production.
- **Escape hatch:** a `Stt:Streaming:FinalReconcile` flag that additionally runs one whole-utterance decode at the end and prefers it — degrading A+ to a guaranteed-accuracy variant (speed becomes pipelining-only) if real-world measurement ever regresses.

## Edge cases

- **Short command, no internal pause** → one segment = whole utterance → identical to a single batch decode (the natural accuracy floor).
- **`MaxUtteranceMs` cutoff** (hub gate) → channel completes → decorator closes/decodes the final open segment and finalizes normally.
- **Segment decode failure / inner exception** → fall back to a single whole-utterance decode of the full buffered audio (today's behavior). Accuracy preserved, speed forfeited for that turn. If even that fails, surface the existing `SttError` path.
- **Speech resumes after a segment closed** → the closed segment's in-flight decode remains valid; new audio becomes the next segment.
- **Backpressure saturation on CPU** → new phrase boundaries while N decodes are already in flight do **not** spawn more; that audio continues buffering and is decoded when a slot frees. Worst case collapses to batch latency, never an error.

## Configuration

New `Stt:Streaming` section (record `SegmentedSttConfig` in `McpChannelVoice/Settings/SttSettings.cs`):

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `false` | Master switch; when off, behavior is exactly today's. |
| `SegmentSilenceMs` | `350` | Trailing silence that closes a phrase segment (< hub `TrailingSilenceMs`). |
| `MinSegmentMs` | `800` | Minimum speech in a segment before it may close; shorter merges forward. |
| `MaxInFlightDecodes` | `1` | Concurrent background decodes (raise on GPU). |
| `FinalReconcile` | `false` | Also run a whole-utterance decode at the end and prefer it. |

Must be mirrored into `appsettings.json` / `appsettings.Development.json` and `DockerCompose/docker-compose.yml` per the project's env-var rule (these are non-secret config, so no `.env` entry). No new secrets.

## Metrics

Reuse `VoiceEvent` / `VoiceMetric`. `SttLatencyMs` is already measured around the transcription call in `WyomingSatelliteHost.TranscribeAndReplyAsync`; with A+ it should drop, which is the headline signal. Add a lightweight `SttSegments` count (segments decoded per utterance) to observe overlap effectiveness on the dashboard. No new dashboard work required in this scope beyond the enum addition.

## Testing strategy

- **Unit — `UtteranceSegmenter` / decorator segmentation:** synthetic RMS frame sequences (same style as existing `SilenceGate` tests) asserting boundaries fire at the segment threshold, merge-forward on short bursts, trailing-silence drop, and final short-segment merge-backward.
- **Unit — `SegmentedSpeechToText` orchestration:** a fake inner `ISpeechToText` asserting concatenation order, backpressure cap (≤ N concurrent calls), single-segment == passthrough, decode-failure fallback to whole-utterance, and that the public result is a single `TranscriptionResult`.
- **Integration — WER gate:** corpus-driven accuracy comparison described above; the production enable-decision is gated on it.

## Open questions / future work

- **Per-request `initial_prompt` context-carry (spike, out of v1 scope):** feeding segment *k−1*'s text as `initial_prompt` / `condition_on_previous_text` into segment *k* would recover cross-phrase context (proper nouns, punctuation) and shrink the residual accuracy gap. Stock `rhasspy/wyoming-whisper` currently applies a *fixed* `--initial-prompt`; whether the Wyoming `transcribe` event accepts a per-request prompt needs verification. v1 ships **fallback-first** without context-carry (the static domain prompt still applies); if the WER gate shows a gap, this is the first lever to add.
- **True live partials / barge-in** — explicitly out of scope (would require surfacing partials to the LLM and a contract change to `IAsyncEnumerable<TranscriptionResult>`).

## Out of scope

- Live partial transcripts to the LLM; barge-in; streaming-native STT servers; GPU provisioning; TTS changes; satellite firmware/provisioning changes.
