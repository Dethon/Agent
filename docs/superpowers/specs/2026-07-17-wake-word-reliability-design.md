# Wake Word Reliability — Custom "ok nabu" Model via livekit-wakeword

**Date:** 2026-07-17
**Status:** Approved
**Scope:** `satellite/` (nabu-satellite Rust crate) + new `satellite/training/` assets. No hub changes.

## Problem

The fran-office satellite (XVF3800 unit, the standardization target) both **misses wakes in
noise/far-field** and **fires spuriously on speech-like audio** (TV dialogue, conversation,
vocals). The deployed operating point is already strict (`--threshold 0.7 --trigger-level 2`
vs 0.5/1 defaults), so threshold tuning is exhausted: the stock model's score distributions
for positives vs speech negatives overlap too much for any operating point to work.

## Root cause

The stock `ok_nabu.onnx` classifier (HA/Nabu-trained, provenance undocumented — it is absent
from openWakeWord's own model zoo) was trained on **English TTS voices**. The household says
"ok nabu" **Spanish-style ("o-ké ná-bu")**, far-field, in reverb and background noise. The
model has never seen its real input distribution.

## Rejected path: Speex NS + Silero VAD (the original advice)

Evidence-based rejection, all claims verified against primary sources:

- Silero VAD in openWakeWord is a **post-hoc gate that only rejects non-speech**
  (`model.py`: predictions zeroed when `max(vad.prediction_buffer[-7:-4]) < vad_threshold`).
  Our false accepts ARE speech — a speech gate passes them. Zero benefit, and it can only
  worsen far-field recall (quiet speech scoring below the VAD bar).
- Speex NS is scoped by openWakeWord's own README to "relatively constant background noise";
  issue #127 documents it failing on competing speech / low SNR. The XVF3800 already does
  firmware NS/beamforming — Speex would re-process an already-processed signal.
- Both features default OFF in openWakeWord, and wyoming-openwakeword v2+ (current HA stack)
  **removed them entirely** (depends on `pyopen-wakeword`, which has zero NS/VAD code).
- Silero VAD's standard ONNX cannot run under tract anyway: it requires a real `If` operator
  (sonos/tract#1029, open). Moot given the above.

## Chosen approach: retrain the classifier head with livekit-wakeword

[livekit-wakeword](https://github.com/livekit/livekit-wakeword) (Apache-2.0) trains only the
classifier head of the exact pipeline the satellite already runs:

- Its frozen frontend (`melspectrogram.onnx` + Google `speech_embedding`) is
  **byte-identical (SHA256-verified) to the openWakeWord v0.5.1 assets embedded in the
  satellite**.
- Exported classifier contract: input `embeddings` `[batch,16,96]` f32 → output `score`
  `[batch,1]` f32 — identical to `ok_nabu.onnx`. **Byte-for-byte drop-in; `detector.rs`
  unchanged if the spike passes.**
- Verified published results (their eval harness, 25h validation): stock openWakeWord DNN →
  `conv_attention` head = FPPH 8.50 → 0.08 (~100×), recall 68.6% → 86.1% (+17pp). Their
  intermediate data point (same DNN arch, their training pipeline) already cuts AUT ~1.7×
  (0.0720 → 0.0423) — so even the fallback head is a win.
- `prod.yaml` includes exactly the robustness we're missing: RIR reverb convolution (p=0.5),
  MUSAN background mixing at 5–15 dB SNR, 3 augmentation rounds, EQ/distortion, focal loss,
  phonetic-neighbor adversarial negatives, ACAV100M general-speech negative pool
  (~2000 h), `max_negative_weight: 3000`, `target_fp_per_hour: 0.1`, checkpoint averaging,
  and a final threshold sweep that emits a recommended operating point.

Training runs on the local WSL2 NVIDIA GPU (their reference is 1× L40S; consumer GPUs are
supported at reduced `tts_batch_size`).

## Phase 0 — tract-compatibility spike (the gate)

The one real technical risk: the `conv_attention` export is opset 18 with fused
`LayerNormalization` and Shape/Gather/Slice dynamic-batch chains (decomposed
`nn.MultiheadAttention`). tract 0.23 support is unverified.

1. **tract load-test** — Rust integration test in `satellite/tests/`: load a conv_attention
   ONNX with `with_input_fact(0, f32::fact([1,16,96]))` → `into_optimized()` →
   `into_runnable()` → run a dummy inference. Probe with their shipped
   `examples/resources/hey_livekit.onnx` (medium).
2. **Toolchain sanity run** — their `configs/test.yaml` scale (`n_samples: 100`,
   `steps: 500`, `tiny`) on the WSL2 GPU: validates CUDA-in-WSL + system deps
   (`espeak-ng`, `libsndfile1`, `ffmpeg`, `sox`) + dataset downloads, and produces a
   conv_attention ONNX from **our** export path to feed check 1 (their example could differ
   from current code's export).

**Decision gate:** conv_attention loads → prod config as-is. Fails → retry after
`onnx-simplifier` with batch pinned to 1 (folds Shape chains to constants). Still fails →
`model_type: dnn` fallback (same op set as today's model, guaranteed compatible). Record the
outcome in this doc.

**Gate outcome (2026-07-17):** conv_attention loads and runs under tract 0.23 after onnxsim batch=1 (zeros-input score = 0.008028805)

## Training pipeline (`satellite/training/`)

Committed: `ok_nabu.yaml` (adapted from `prod.yaml`) + `README.md` with exact run commands +
pinned `livekit-wakeword` version. Datasets/checkpoints untracked; eval results committed
under `satellite/training/results/`.

**Positives — Spanish-first, three sources:**

1. **VoxCPM2 multilingual backend, Spanish** — correct phonetics (primary). Documented as
   lower voice-diversity than the English path; eval decides its weight.
2. **English Piper backend (904-speaker SLERP) with phonetic-variant spellings** as extra
   `target_phrases` (e.g. `"okay nabboo"`, `"oh keh nah boo"` — tuned by listening to
   generated samples) — recovers voice diversity. Whether one run can mix TTS backends or
   `generate` runs twice with merged output before `augment` is a spike finding (stages are
   separable), not a blocker.
3. **Real recordings** — a few dozen "ok nabu" per household speaker, captured with
   `arecord` on the Pi (16 kHz mono S16LE, production mic path). Most go into training
   positives; a **held-out set is reserved for on-device threshold calibration**.

**Negatives:** keep prod machinery (ACAV100M, MUSAN-as-negative-class, phonetic-neighbor
generator) and add `custom_negative_phrases` in **both languages** (Spanish near-misses and
partial phrases plus English-style ones). Speech-triggered false accepts are exactly what
`max_negative_weight` + `target_fp_per_hour: 0.1` target — keep prod values.

**Augmentation:** keep prod (RIR p=0.5, MUSAN 5–15 dB, `rounds: 3`, EQ/distortion).

**Model size:** train `small` (32d×1) and `medium` (128d×2); benchmark both under tract on
the A53 via the existing per-chunk `wake inference` debug timing; ship the largest with
comfortable headroom in the 80 ms chunk budget.

## Satellite integration

- Replace `satellite/models/ok_nabu.onnx` bytes — same filename, same `include_bytes!`,
  same input fact, same `[[0,0]]` score read. Zero `detector.rs` changes expected.
- **Operating point:** start at livekit's swept threshold with `--trigger-level 1`
  (0.7/2 was compensation for the bad model), then calibrate on-device against held-out
  real recordings + `RUST_LOG=debug` score logging. Final values updated in
  `deploy/nabu-satellite.service` and `scripts/provision-satellite-rs.sh`.
- **Fixtures:** new Spanish-pronounced `tests/fixtures/ok_nabu.wav`; `fires_once`,
  `silent_on_silence`, `detectors_share_one_model_bundle` keep their logic.
- **Provenance:** update `models/LICENSE-models.md` (classifier = self-trained artifact;
  frontend unchanged Apache-2.0). Old model recoverable via git history.

## Risks & fallbacks

| Risk | Mitigation |
|---|---|
| tract rejects conv_attention | Phase-0 gate; onnx-simplifier batch=1 retry; `dnn` head fallback |
| VoxCPM2 Spanish too weak | Eval recall on real held-out set exposes it; rebalance toward Piper variants + real recordings |
| A53 CPU budget | Benchmark `small`/`medium` with existing debug timing; ship what fits |
| New model worse in the field | Binary swap reversible (keep previous release binary); staged rollout with debug scoring before declaring done |
| Training irreproducible | Pin package version + config + seed; commit eval JSON/DET curve |

## Testing & acceptance

1. Spike test written first (red without a compatible model, green when the artifact loads
   and runs).
2. Crate tests pass with the new fixture; qemu smoke keeps `--no-wake` (known f16 issue).
3. On-device protocol on the fran-office Pi, **same protocol before and after**: wake trials
   at ~2 m and ~4 m, quiet and TV-at-conversational-volume; overnight TV soak for spurious
   wakes.
4. **Acceptance:** ≥9/10 wakes quiet far-field, ≥8/10 with TV, <1 spurious wake per TV
   evening.
5. Per-chunk inference timing confirmed within budget via `RUST_LOG=debug`.

## Non-goals

- No Speex NS, no Silero VAD, no VAD-style gating of any kind.
- No hub/McpChannelVoice changes; no changes for Jabra (discarded) or WSL (test-only) units.
- No new CLI flags or runtime model selection — the model stays an embedded artifact.
