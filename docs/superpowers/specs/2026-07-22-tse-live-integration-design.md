# TSE Live Integration (Phase 2) — Design

**Date:** 2026-07-22
**Status:** Approved
**Branch:** noise
**Predecessor:** `2026-07-22-stt-enhancement-eval-design.md` (round-1 verdict: TSE passes both
backends decisively — +80.5 % rel WER at ≤5 dB competing speech on whisper-medium, +57.6 % on
prod whisper-base, zero clean regression; GTCRN/DFN3 denoisers fail and are eliminated).

## Motivation & Scope Shift

Round 1's spec gated production work on an offline field-recorded corpus. The owner redefined
phase 2: **deploy TSE into the real pipeline behind config and validate it live** — on the
current pi5 prod stack immediately (slow but real), on the AI 370 inference box when it
arrives. Live usage over the actual TV is more realistic than any scripted session; the
fail-open design makes the risk profile acceptable (worst case = slower noisy turns during
the trial, never lost commands, killable by config).

## Goals

- A `tse-extractor` sidecar service any hub deployment can call.
- Hub STT-path integration as a config-gated, fail-open decorator — zero behavior change when
  off or when the sidecar is unreachable.
- Live-trial instrumentation: metrics for invoke/skip/fail/latency, plus an opt-in capped
  audio audit trail for spot-listening extraction quality.
- Trial readout criteria that let the owner decide keep/expand/revert from the dashboard.

## Non-Goals

- No denoiser conditions (eliminated in round 1 — do not re-propose).
- No changes to the speaker gate, endpointing, wake, satellite, or TTS. Gate and endpointing
  keep consuming **raw** audio; all field-measured calibrations stay valid.
- No streaming TSE (utterance-scoped extraction only) and no re-enrollment — conditioning uses
  the existing prod `voices/<name>/enroll-*.wav` takes, all of them (the eval's take-exclusion
  was a no-leakage measure for scoring against enrollment-derived clips; live utterances are
  never enrollment takes, so production conditions on everything).
- No TSE-assisted re-verification of gate rejects (v2 candidate; this trial only *observes*
  gate behavior in noisy scenes).

## Architecture

### 1. `tse-extractor` sidecar service

- New compose service: Python container wrapping the persistent-worker pattern proven in the
  eval (WeSep demo checkpoint, BSRNN + ECAPA, 16 kHz; worker loads the checkpoint once and
  streams one extraction per request).
- Checkpoint + wesep source provisioned into a named model volume on first start
  (idempotent entrypoint, mirroring the eval's `tse_env_setup.sh` pins; piper/whisper-data
  precedent). The container is env-isolated, so it is NOT bound to the harness's torch 2.1.2
  pin — it uses a current torch with aarch64 wheels for the pi5.
- Mounts the hub's `voices` volume read-only. Startup builds one enrollment embedding per
  speaker directory (concatenated takes); a per-directory content signature (file names +
  sizes + mtimes) invalidates the cache when enrollment changes.
- API: `POST /extract?speaker=<name>` — body 16 kHz mono S16LE WAV, response same format,
  ~same length as input; `GET /health` — ready + loaded speakers; unknown speaker → 404
  (hub falls back to raw). Single-flight worker: requests are serialized (utterances are
  seconds apart in practice; the hub's deadline covers pileups).

### 2. Hub decorator (`TseSpeechToText : ISpeechToText`)

- Registered in DI wrapping `SegmentedSpeechToText`; no call-site changes.
- `TranscriptionOptions` gains `TargetSpeaker` (string?, the gate's conclusive identity, or
  the accepted top-1 candidate when identification is ambiguous) and `NoiseFloorRms`
  (double, from `CaptureStats.FloorRms` — the floor frozen at first speech).
  `WyomingSatelliteHost` populates both where it already holds the verification result.
- Invocation policy (`Mode`):
  - `Off` — pass-through (default until the trial starts).
  - `Auto` — invoke iff `TargetSpeaker != null` AND `NoiseFloorRms >= NoiseFloorThreshold`.
  - `Always` — invoke whenever `TargetSpeaker != null` (diagnostic mode).
- Invocation: buffer the capture's replayable chunks to one WAV, POST to the sidecar with
  `TimeoutMs` deadline, feed the extracted audio to the inner STT. **Fail-open on every
  path**: mode Off, no speaker, floor below threshold, HTTP error, timeout, malformed reply →
  the inner STT receives the raw audio, byte-identical to today.
- Rationale for the floor trigger (round-1 evidence): whisper confidence does NOT
  discriminate TV contamination (mean score 0.73 in cells where raw WER was 93–100 %), so a
  retry-on-low-score policy would miss the failures that matter. The pre-speech floor is a
  direct scene-noise measurement the hub already computes and publishes.

### 3. Audit trail (opt-in)

- In the decorator, when extraction runs and `AuditDir` is configured: write
  `<utc-timestamp>-<satellite>-<speaker>/{mixture.wav, extracted.wav, meta.json}`
  (meta: speaker, floor, latency ms, deadline hit?, satellite id). Ring-capped at
  `AuditMaxPairs` (default 50), oldest pruned. Off by default; household audio stays local
  and bounded.

### 4. Metrics

- New voice metric values with **explicitly pinned ints** (per the enum-corruption lesson):
  TSE invoked / skipped-quiet / skipped-no-speaker / failed counts and `TseLatencyMs`.
  Published through the existing voice metrics path; the dashboard's existing breakdowns
  surface them. The latency distribution on pi5 vs AI 370 IS the deployability measurement —
  no separate benchmark tooling.

### 5. Config & infrastructure (same change as the code, per repo rule)

- `TseSettings`: `Mode` (Off is the kill switch — no separate Enabled flag), `Endpoint`,
  `TimeoutMs`, `NoiseFloorThreshold`, `AuditDir?`, `AuditMaxPairs`.
- `DockerCompose/docker-compose.yml`: the `tse-extractor` service (model volume, voices RO
  mount) + `TSE__*` env skeletons on `mcp-channel-voice`; `appsettings.json` keys with
  placeholder values. Non-secret config only — nothing for `.env`.
- pi5 trial guidance: `Mode=Auto`, `NoiseFloorThreshold` provisionally calibrated from the
  FloorRms metrics history already in Redis (quiet-room vs TV-on bands), generous `TimeoutMs`
  (~90 s — pi5 CPU extraction is expected to be slow; Auto keeps quiet turns fast). AI 370:
  same config, tighter deadline after the dashboard shows the real latency.

## Trial Readout (keep/expand/revert — owner's call, from the dashboard + audit pairs)

1. `TseLatencyMs` distribution on pi5, later AI 370 (deployability).
2. Invoked-turn transcript quality vs memory of raw behavior — audit pairs + daily use.
3. Quiet-path regression check: skip rate ≈ 100 % in quiet scenes, unchanged latency there.
4. Observational: gate accept/identify rates vs floor level in noisy scenes (feeds the v2
   TSE-assisted-reverify question; ERes2NetV2's TV band is still unmeasured).

## Testing

- TDD on the decorator: policy matrix (mode × speaker × floor × sidecar outcome →
  invoked/skipped/fallback), fail-open on timeout/error/404, audit ring write + prune,
  options plumbing (host sets `TargetSpeaker`/`NoiseFloorRms`). Sidecar client faked via
  `HttpMessageHandler`.
- Sidecar: container integration test (`Tests/Integration`, needs the compose service) —
  `/health`, then `/extract` on a fixture WAV asserting format/length; unknown speaker → 404.
- Live validation is the trial itself.

## Risks

- **pi5 latency**: extraction may run tens of seconds per clip on Cortex-A76; mitigated by
  Auto mode (quiet turns unaffected), the deadline fail-open, and the AI 370 arriving soon.
- **English/VoxCeleb checkpoint on Castilian**: round 1 says identity transfer works; live
  audio is the real test — the audit trail is the instrument for catching failure modes.
- **aarch64 torch/wesep availability**: the container needs arm64 wheels; if the demo stack
  resists arm64, the fallback is running the sidecar on the WSL box during the pi5 trial
  (compose profiles / endpoint config make this a config choice, not a code change).
- **Gate under TV**: unmeasured; if the gate rejects real commands in loud TV, TSE never gets
  the chance to help those turns. The trial's observational metrics quantify this before any
  v2 work.
