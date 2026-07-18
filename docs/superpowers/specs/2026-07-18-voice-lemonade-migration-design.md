# Voice STT/TTS Migration to Lemonade — Design Spec

**Date:** 2026-07-18
**Status:** Draft — for review
**Component:** `McpChannelVoice` + `DockerCompose`

## Goal

Replace the two Wyoming voice-engine containers (`wyoming-whisper` STT, `wyoming-piper` TTS) with a **single AMD Lemonade Server** exposing OpenAI-compatible speech APIs: Vulkan-accelerated Whisper transcription and streaming Kokoro synthesis. Target host is a **Ryzen AI 9 HX 370 (Strix Point)** mini-PC running the stack in Docker on Linux. This is a **hard cutover** — no Wyoming fallback.

## Context

Today the voice hub (`McpChannelVoice`) is a Wyoming **client** in two independent roles:

1. **Satellite transport** — `WyomingSatelliteHost` dials the hardware satellites over the Wyoming protocol (mic in, audio out). **This does not change.**
2. **Engine backends** — `WyomingSpeechToText` connects to `wyoming-whisper` and `WyomingTextToSpeech` connects to `wyoming-piper`, both over Wyoming. **These are what this migration replaces.**

The engine backends sit behind two contracts, `ISpeechToText` and `ITextToSpeech`, so replacing them is a localized swap of the two inner client classes plus their config and DI wiring.

The move is driven by wanting **iGPU-accelerated Whisper `medium`** (which the current faster-whisper/CTranslate2 path cannot do on AMD — it is CUDA/CPU only) and consolidating STT+TTS into one OpenAI-compatible service. Castilian voice quality has been **explicitly deprioritized** for speed and simplicity, so Kokoro's Latin-American (es-419) Spanish is accepted; there is no Piper `es_ES` voice in Lemonade.

## Goals

- One Lemonade container serving OpenAI `/v1/audio/transcriptions` (Whisper) and `/v1/audio/speech` (Kokoro).
- STT device tier (**CPU / iGPU / NPU**) selectable from a **single environment variable**.
- Preserve the existing gibberish-rejection confidence gate.
- Preserve **streaming TTS** (audio begins playing before the whole utterance is synthesized).
- Hard cutover: delete the Wyoming STT/TTS clients and their containers.

## Non-Goals

- Improving Castilian/Spanish voice quality (deliberately out of scope).
- Changing the hub↔satellite Wyoming transport, segmentation, silence gate, follow-up windows, chimes, or playback pump.
- Local LLM inference (the agent LLM stays remote on OpenRouter).
- Migrating to Lemonade's OpenAI Realtime WebSocket API (the batch transcription + streaming speech endpoints are sufficient).

## Key Decisions (with rationale)

| Decision | Rationale |
|---|---|
| **Lemonade, not Speaches** | Speaches STT is faster-whisper/CTranslate2 = CUDA/CPU only, so it cannot use the 890M iGPU or the NPU. Lemonade's whisper.cpp backend runs on the iGPU (Vulkan/ROCm) on Linux. |
| **Hard cutover, no fallback flag** | Simpler; avoids maintaining two client paths and dual config. |
| **Preserve the confidence gate** | Lemonade returns `avg_logprob` + `no_speech_prob` via `response_format=verbose_json` (verified in source — the "json only" docs are stale). The gate is repointed to these fields; no functional loss. |
| **Streaming TTS via `stream_format=audio`** | Verified incremental: the Kokoros backend splits text into ~10-word chunks, synthesizes each separately, and Lemonade forwards the PCM byte-for-byte with no buffering. Real first-audio latency benefit. |
| **`STT_BACKEND` env var + entrypoint** | Lemonade selects its backend via `lemonade config set` / `config.json`, not a native env var. A thin container entrypoint translates one env var into the right config + model. |
| **Custom Lemonade image** | The stock image ships only CPU+Vulkan+ROCm whisper. The NPU tier needs the FastFlowLM (`flm`) binary baked in. |
| **iGPU (`gpu`) is the default tier** | Runs literal `medium`, mature GPU path. NPU is an optional, experimental power-efficiency upgrade. |

## Architecture

### Stays unchanged
`WyomingSatelliteHost`, `WyomingClient`, the `WyomingProtocol/*` classes, `SilenceGate`, `SegmentedSpeechToText`, `SilenceTrimmingTextToSpeech`, `TranscriptDispatcher` (repointed only), playback/announcement/follow-up services. The `Segmented*` and `SilenceTrimming*` wrappers are backend-agnostic decorators and are **kept**.

### Deleted
- `McpChannelVoice/Services/Stt/WyomingSpeechToText.cs`
- `McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs`
- `WyomingSttConfig`, `WyomingTtsConfig` (in `Settings/`)
- `McpChannelVoice/Services/WyomingHealthProbeService.cs` (probes wyoming-whisper/piper)
- Compose services: `wyoming-whisper`, `wyoming-piper`, `piper-voice-fetch`, and the `./wyoming-whisper` build context

### New / changed components

**`OpenAiSpeechToText : ISpeechToText`** (new, `Services/Stt/`)
- Buffers the segment's `IAsyncEnumerable<AudioChunk>` into a WAV blob (mono s16le at the incoming rate — satellites send 16 kHz).
- `POST {BaseUrl}/audio/transcriptions` as multipart: `file` (WAV), `model`, `response_format=verbose_json`, `language` (from `TranscriptionOptions`/config).
- Parses the verbose_json response: `text`, and the **duration-weighted mean** of the response's segment `avg_logprob` / `no_speech_prob` (one POST = one segment here, but the body may carry several) → `TranscriptionResult { Text, Language, AvgLogProb, NoSpeechProb }`. `Confidence` and `CompressionRatio` are left null (Lemonade provides neither). Note `SegmentedSpeechToText` then does its own duration-weighted merge across the utterance's segments — the plumbing already exists.

**`OpenAiTextToSpeech : ITextToSpeech`** (new, `Services/Tts/`)
- `POST {BaseUrl}/audio/speech` with `model` (`kokoro-v1`), `voice`, `stream_format=audio`, `response_format=pcm`.
- Consumes the response **incrementally**: `HttpCompletionOption.ResponseHeadersRead` + `Content.ReadAsStreamAsync()`, reads fixed-size buffers in a loop, keeps the response/stream alive for the whole enumeration.
- **Odd-byte carry:** raw PCM reads are not guaranteed 2-byte aligned; hold a 0/1-byte remainder and prepend it to the next read before any int16 interpretation.
- Resamples each block 24000→22050 Hz via a **stateful** resampler, yields `AudioChunk { Data, Format = 22050/2/1 }` per resampled block.

**`PcmStreamResampler`** (new, `Services/Tts/` or `Services/Audio/`)
- 24000→22050 is the exact rational **147/160** (0.91875). Implement as a stateful rational resampler that carries the fractional phase and the previous input sample(s)/filter history across calls, so there are no clicks at chunk boundaries.
- State lives in the caller's async-iterator locals (one enumeration = one continuous stream ⇒ per-utterance, inherently thread-safe). **Never static/shared.**
- Linear interpolation is adequate for a gentle ~8% downsample of speech; a short windowed-sinc/polyphase FIR is an optional quality upgrade, not required to ship.

**`TranscriptDispatcher`** (changed)
- Today it drops when `Confidence is { } c && c < confidenceThreshold`. Repoint to threshold on `AvgLogProb` (drop if present and below a floor) and/or `NoSpeechProb` (drop if present and above a ceiling). Keep the **fail-open on null** behavior. The `VoiceEvent` metric already carries `AvgLogProb`/`NoSpeechProb`; `Confidence`/`CompressionRatio` will simply be null.

**Settings + DI** (changed, `Settings/` + `Modules/ConfigModule.cs`)
- New `OpenAiSttConfig` (`BaseUrl`, `Model`, `Language`, `AvgLogProbThreshold`, `NoSpeechProbThreshold`) and `OpenAiTtsConfig` (`BaseUrl`, `Model`, `Voice`, `Speed`).
- `ConfigModule` wires `SegmentedSpeechToText.Wrap(new OpenAiSpeechToText(...))` and `SilenceTrimmingTextToSpeech.Wrap(new OpenAiTextToSpeech(...))`. Register a named/typed `HttpClient` for Lemonade.

### STT data flow
```
satellite mic → SilenceGate / segmentation (unchanged)
  → SegmentedSpeechToText (unchanged wrapper)
    → OpenAiSpeechToText: WAV(16k) → POST /audio/transcriptions?response_format=verbose_json
      → TranscriptionResult{ text, avg_logprob, no_speech_prob }
  → TranscriptDispatcher (repointed gate) → dispatch or drop
```

### TTS data flow
```
reply text → SilenceTrimmingTextToSpeech (unchanged wrapper, already streaming-safe)
  → OpenAiTextToSpeech: POST /audio/speech?stream_format=audio&response_format=pcm
    → chunked 24kHz PCM (read incrementally)
    → odd-byte carry → stateful 24k→22.05k resample
    → yield AudioChunk @ 22050
  → satellite playback (unchanged)
```

## STT acceleration tiers

Selected by the single env var **`STT_BACKEND`** ∈ {`cpu`, `gpu`, `npu`}:

| `STT_BACKEND` | Lemonade backend | Engine | Model | Device needs | Maturity |
|---|---|---|---|---|---|
| `cpu` | `whispercpp.backend=cpu` | whisper.cpp | whisper-medium | none | proven |
| `gpu` *(default)* | `whispercpp.backend=vulkan` (or `rocm`) | whisper.cpp | whisper-medium | `/dev/dri` (+`/dev/kfd` for rocm) | proven |
| `npu` | `whispercpp.backend=npu` → FastFlowLM | FLM | whisper-large-v3-turbo *(forced)* | `/dev/accel/accel0` + host `amdxdna` | experimental |

- CPU and GPU are the **same** whisper.cpp engine and model — only the device flips.
- NPU is a **different** engine (FastFlowLM) and **forces `large-v3-turbo`** (FLM ships no `medium`). So `STT_BACKEND=npu` also changes the model the hub requests. `STT_BACKEND` is the single source of truth that expands to `{ Lemonade backend, hub STT model, device mounts }`.

## Configuration & Environment

Per the project's env-var rules, all of the following land in the same change:

- **`DockerCompose/docker-compose.yml`**
  - New `mcp-lemonade` service: custom image build (`./lemonade`), `ports: 13305`, `environment: STT_BACKEND=${STT_BACKEND:-gpu}`, device mounts `/dev/dri`, `/dev/kfd`, `/dev/accel/accel0` (unused ones are harmless), a model/HF cache volume.
  - Remove `wyoming-whisper`, `wyoming-piper`, `piper-voice-fetch`.
  - Agent/hub service gains STT/TTS base-URL + model env (or via appsettings) pointing at `http://mcp-lemonade:13305/v1`.
- **`DockerCompose/lemonade/Dockerfile`** (new): `FROM` stock Lemonade image + install FastFlowLM `flm`; entrypoint script maps `STT_BACKEND` → `lemonade config set whispercpp.backend=…` (+ ensure/pull the tier's model) → `lemonade serve`.
- **`appsettings.json` / `appsettings.Development.json`**: `Stt.OpenAi` (BaseUrl, Model, Language, thresholds) and `Tts.OpenAi` (BaseUrl, Model=`kokoro-v1`, Voice, Speed). Remove `Stt.Wyoming` / `Tts.Wyoming`.
- **`DockerCompose/.env`**: none required — Lemonade is a local, unauthenticated service (no secret). `STT_BACKEND` is non-secret config and lives in compose/appsettings, not `.env`.

## Error handling

- STT: non-2xx or malformed body → throw so the existing STT error path fires (as `WyomingSpeechToText` did on a Wyoming `error` event). Empty text → the gate drops it (unchanged).
- TTS: non-2xx before/at stream start → throw so the playback loop's `onError`/`OnFailed` path fires (parity with today's behavior on a Wyoming `error`). A mid-stream failure aborts the enumeration and surfaces as a synthesis error.
- Resampler: must never emit partial samples; the odd-byte remainder handling is a correctness requirement, not an optimization.

## Testing strategy (TDD)

Tests are unit-level with a mocked Lemonade HTTP endpoint. Implement in this order (riskiest first):

1. **`PcmStreamResampler`** — feed a known 24 kHz signal split across arbitrary (including odd-length) chunk boundaries; assert the concatenated 22050 output matches a whole-buffer resample within tolerance and has **no discontinuity at boundaries** (the click regression). This is the highest-risk unit.
2. **`OpenAiTextToSpeech`** streaming — mock a chunked PCM response (including a split mid-sample); assert it yields `AudioChunk`s @ 22050 incrementally (first chunk before body completes), with correct odd-byte handling.
3. **`OpenAiSpeechToText`** — mock a `verbose_json` body; assert `TranscriptionResult` carries `text`, `AvgLogProb`, `NoSpeechProb`; assert text-only/`json` responses degrade gracefully (null signals).
4. **`TranscriptDispatcher`** gate repoint — drops on low `AvgLogProb` / high `NoSpeechProb`, dispatches otherwise, fails open on null.
5. **DI/config** — `ConfigModule` resolves `ISpeechToText`/`ITextToSpeech` to the wrapped OpenAI clients from settings.
6. **Compose / Dockerfile / entrypoint** — integration-level: `STT_BACKEND` selects the right backend/model; container exposes `/v1/audio/{transcriptions,speech}`.

## On-box validation checklist (not assumed by the design)

- [ ] `verbose_json` returns `avg_logprob` + `no_speech_prob` from the actually-deployed Lemonade/whisper.cpp-rocm binary (docs understate it; confirm empirically).
- [ ] iGPU Vulkan whisper loads on the 890M (`/dev/dri`) and hits ~0.08–0.1 RTF, not a silent CPU fallback (~0.3–0.5).
- [ ] Streaming constraints: confirm `response_format=pcm` + `stream_format=audio` stream incrementally and whether `speed` must stay 1.0.
- [ ] Resampled TTS sounds correct (no pitch shift, no boundary clicks) through the satellite's fixed 22050 sink.
- [ ] NPU tier (if used): `amdxdna` kernel + firmware ≥ 1.1.0.0, `flm serve --asr 1` runs, `whisper-large-v3-turbo` transcribes; treat as experimental.

## Risks & open questions

- **NPU tier maturity** — FastFlowLM Linux and Lemonade Linux-NPU both shipped ~March 2026 with real load-failure reports. The design keeps `cpu`/`gpu` as the shipping path and marks `npu` experimental.
- **Exact NPU selection trigger** — whether `whispercpp.backend=npu` routes to FLM vs. requiring the FLM model be requested directly had conflicting evidence; resolve during entrypoint implementation + on-box test.
- **verbose_json is undocumented** in Lemonade (works per current source); a future build could change it. Mitigation: the gate already fails open, so a regression degrades gracefully rather than breaking.
- **STT input format** — confirm Lemonade transcription accepts our WAV (it documents "wav only"); package mono s16le.

## References (verified 2026-07-18)

- Lemonade transcription is a transparent pass-through; verbose_json emits `avg_logprob`/`no_speech_prob` (not `compression_ratio`): `lemonade/src/cpp/server/backends/whispercpp/whispercpp_server.cpp`, `lemonade-sdk/whisper.cpp-rocm/examples/server/server.cpp`.
- whisper.cpp NPU backend is Windows-only; Linux NPU whisper is FastFlowLM (`flm serve --asr 1`, turbo-only): same `whispercpp_server.cpp` + Lemonade issue #1472.
- Kokoro streaming is incremental (~10-word chunks, byte-for-byte forward): `lemonade-sdk/Kokoros` + Lemonade `streaming_proxy.cpp`.
- 24000→22050 = 147/160; stateful rational resampling required (CCRMA / libsamplerate).
