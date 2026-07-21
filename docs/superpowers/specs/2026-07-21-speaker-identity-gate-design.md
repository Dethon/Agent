# Speaker-Identity Gate (Background-TV Rejection)

**Date:** 2026-07-21 · **Branch:** `noise` · **Status:** Approved

## Problem

With a TV in the room, captures containing only TV speech still reach STT and the
agent. Field calibration (2026-07-20, see the adaptive-endpointing spec,
refinement 7) proved the two existing gate families cannot close this:

- **Level prominence is capped by the mic.** The XVF3800 firmware AGC — which
  stays ON (it is what makes far-off speech decodable) — normalizes every
  speech-like source toward one target level. Measured: TV leak turns at
  14.4–18.9 dB over floor, a real command at 11.2 dB. TV can be *more*
  prominent than the user.
- **STT statistics do not separate.** A clean TV monologue scored confidence
  0.62 / no_speech_prob 0.12 — indistinguishable from real commands — while a
  genuine command scored 0.42.

What TV can never imitate is the *identity* of the voice: the household is 2–4
known speakers; TV is an endless parade of strangers. Speaker identity is
spectral, not level-based, so it survives the AGC.

## Design

### Placement and flow

Verification runs in the hub (`McpChannelVoice`), at the top of
`WyomingSatelliteHost.TranscribeAndDispatchAsync`, **before** STT:

1. Capture ends `Ended` (the existing SilenceGate path, including the floor
   demote, is unchanged and runs first — it is free and removes the
   convergence-latch class before any model runs).
2. The hub embeds the capture's speech audio and takes the max cosine
   similarity against the enrolled household profiles.
3. **Match** → proceed to whisper exactly as today.
4. **No match** → publish a rejection metric and return `false` — the existing
   `!dispatched` path in `FollowUpConversation` ends the conversation and
   re-arms wake. No `FollowUpConversation` changes. Rejected captures never
   reach whisper (saves the 42–92 s Pi decodes TV was burning).

### Embedding audio selection

`UtteranceCapture` tags each buffered chunk with the gate's speech/silence
classification as it feeds. The verifier embeds the concatenated
speech-classified chunks (better SNR than the whole capture, which includes
the pre-roll gap and trailing background). Fallback: whole capture if no
chunk was classified speech (cannot happen for an `Ended` capture that passed
minSpeech, but the code path is total).

### Model and runtime

- `Microsoft.ML.OnnxRuntime` (ships linux-arm64 natives; runs on the pi5 hub
  container) executing a small speaker-embedding model: **CAM++ (3D-Speaker)**
  primary candidate, **WeSpeaker ResNet34** fallback — both ONNX-exportable,
  7–25 MB, EER ≈ 1% on VoxCeleb. Final pick is benchmarked on the Pi 5 during
  implementation; target < 500 ms per capture (vs 42–92 s whisper — noise).
- These models consume 80-dim log-mel filterbank features (25 ms window,
  10 ms hop, 16 kHz), not raw PCM. The hub gets a small C# fbank frontend
  (`McpChannelVoice/Services/Verification/`), unit-tested against golden
  vectors generated offline with the reference implementation.
- The model file is baked into the Docker image at build time from a pinned
  URL with SHA256 verification (no runtime download, no new secret).
- **Final pick: `wespeaker_en_voxceleb_CAM++.onnx`** (CAM++/3D-Speaker), from
  the sherpa-onnx `speaker-recongition-models` release:
  `https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx`,
  pinned at
  `sha256:c46fad10b5f81e1aa4a60c162714208577093655076c5450f8c469e522ec54ef`.

### Enrollment

- A `voices/` volume is mounted into the hub container:
  `voices/<identity>/*.wav` — 3–5 short recordings per person, 16 kHz mono
  S16LE.
- At startup the hub embeds every WAV, L2-normalizes, and averages per person
  into one profile. Embeddings are cached beside the WAVs
  (`voices/<identity>/profile.json`, invalidated when WAVs change).
- **`scripts/enroll-voice.sh <name>`** records the enrollment WAVs *through
  the satellite mic itself* (`arecord` on the Pi against the auto-detected
  XVF3800 card, same auto-detect idiom as satellite provisioning): prompts a
  countdown, records N (default 5) utterances of ~4 s each, and copies them
  into the hub's voices volume (scp target parameter, defaulting to the pi5
  host path). Recording through the same mic + AGC chain the gate hears keeps
  enrollment domain-matched — a phone recording has a different channel
  response and would depress similarity scores. The script must
  `systemctl stop nabu-satellite` first and restart it after (the satellite
  process holds the capture device; `nabu-micclock` stays running so the
  XVF3800 capture engine keeps its clock — see satellite/CLAUDE.md).
- Guests are rejected by design until enrolled. That is the point of the gate,
  but it is a real UX consequence: visitors cannot use voice on gated
  satellites.

### Decision policy

- Similarity = max cosine over enrolled profiles.
- Accept iff similarity ≥ `SimilarityThreshold`. Ships at **0.5** (cosine).
  Real CAM++ measurements from the integration test (same audio, sherpa-onnx
  reference confirmed): same-speaker ~0.93, cross-speaker ~0.44–0.55 on
  synthetic voices — so 0.5 sits just above the cross-speaker band as a
  conservative starting point, and is re-tuned per satellite from the published
  field distributions. (The earlier 0.35 guess predated the measured data and
  would have accepted strangers.)
- **Short-utterance skip:** captures with less than `MinVerifySpeechMs`
  (default 800 ms) of gate-classified speech skip verification and pass.
  Sub-second embeddings are unreliable, and a measured real command
  ("¿…tiempo…Valladolid?" at 480 ms speech) must stay safe. Consequence: TV
  blips under 800 ms pass the identity gate — the confidence gate caught all
  observed instances of that class (640 ms blips at conf 0.34–0.36).
- The similarity score is published on **both** outcomes so the threshold is
  tunable from field data (same pattern as FloorRms/TrailingRms):
  - Accepted → existing `UtteranceTranscribed` event gains `Similarity`.
  - Rejected → new pinned `VoiceMetric.UtteranceRejected = 18` carrying the
    capture stats (`PeakRms`/`FloorRms`/`TrailingRms`/`SpeechMs`/`EndReason`)
    plus `Similarity` and `Outcome = "unknown_speaker"`.

### Configuration

`SpeakerVerificationSettings` (new section in `McpChannelVoice`
appsettings.json; non-secret, so no `.env` entry):

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch; also effectively off when the voices folder is empty/missing |
| `ModelPath` | image path | ONNX model location |
| `VoicesPath` | `/voices` | enrollment volume mount |
| `SimilarityThreshold` | model-dependent, conservative | per-satellite override via `SatelliteConfig` |
| `MinVerifySpeechMs` | `800` | skip-verification floor |

Per-satellite: `Verification { Enabled?, SimilarityThreshold? }` override block
following the existing `Gate`/`Resolve*` pattern.
`DockerCompose/docker-compose.yml`: voices volume on `mcp-channel-voice`.

### Failure handling

Fail-open everywhere: missing/corrupt model, ONNX load or inference error,
unreadable voices folder → log a warning once and run without the gate. Voice
availability beats gating. Gate inactivity is visible from the dashboard
without a dedicated metric: `UtteranceTranscribed` events carry
`Similarity = null` whenever verification did not run.

### Testing (TDD, red-green-refactor)

- Unit: fbank frontend vs golden vectors; profile building (L2-norm, mean,
  cache invalidation); accept/reject/short-skip policy against a fake
  verifier; `TranscribeAndDispatchAsync` rejection path (no STT call, metric
  published, returns false).
- Integration: real ONNX model + two-speaker fixture WAVs — same-speaker
  similarity must exceed cross-speaker by a pinned margin; startup profile
  build from a fixture voices folder.
- Field: enable on fran-office only, TV-on test evening, tune
  `SimilarityThreshold` from the published similarity distributions.

## Non-goals

- Agent-signaled conversation close (send_reply "not addressed" flag) —
  complementary semantic layer, separate follow-up spec.
- Voice-driven enrollment ("enroll me" MCP tool) — possible follow-up; the
  WAV folder is v1.
- Per-speaker personalization (routing the *matched* identity into the
  conversation) — the plumbing makes it possible later; out of scope here.
- Satellite (Rust) or Wyoming protocol changes; wake-word changes.

## Known limits

- A guest or unenrolled household member is silently rejected (visible only in
  the dashboard's UtteranceRejected events).
- Sub-`MinVerifySpeechMs` TV blips bypass the gate (confidence gate remains
  the defense there).
- Severe voice changes (illness) can depress similarity below threshold;
  per-satellite threshold override and the published scores are the recourse.
