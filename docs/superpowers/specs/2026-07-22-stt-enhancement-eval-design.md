# STT Enhancement Evaluation — Design

**Date:** 2026-07-22
**Status:** Approved
**Branch:** noise

## Motivation

TV/radio audio contaminates voice-command transcripts. The speaker gate rejects
utterances that aren't a household member, and the endpointing fixes stopped TV from
corrupting segmentation — but when a real command is spoken *over* a loud TV, whisper
still transcribes the mix. Before wiring any preprocessing into the hub, measure
offline whether open-weights enhancement/extraction models actually cut WER in that
regime without hurting clean audio. Latency is out of scope for the eval: the
inference box (AI 370, Lemonade migration) arrives soon.

Production insertion, if any, would be **STT-path only**: the speaker gate and
endpointing keep consuming raw audio so their field-measured calibrations
(thresholds, PeakDropDb, floor freezing, enrollment prototypes) stay valid.

## Goals

- A reusable, staged eval harness where the corpus is an input, so the phase-2 field
  corpus drops in unchanged.
- Round-1 numbers: WER raw vs. enhanced across SNRs for three model families.
- A decision rule for whether any model earns hub-integration work.

## Non-Goals

- No hub/production code changes.
- No streaming/latency measurement (offline only).
- No speaker-gate metric in round 1 (gate keeps seeing raw audio in the intended
  insertion; measuring embedding shift under enhancement is a possible follow-on).
- Phase 2 (field recording with the real TV) is designed but not built in round 1.

## Corpus (phase 1: synthetic)

- **Clean speech:** the enrollment takes from pi5
  (`pi5:/opt/jackbot/DockerCompose/volumes/voices/<name>/*.wav`), 16 kHz mono S16LE,
  recorded through the satellite mic/AGC chain at 5 positions. Reference transcripts
  come from the `enroll-voice.sh` phrase list; the take→phrase mapping is
  deterministic in the script (`idx = (cond + pass) % len(PHRASES)`). Implementation
  verifies the mapping against filenames; fallback if unreliable: transcribe clean
  takes with faster-whisper medium and hand-check (corpus is small).
- **Interference:**
  - *TV dialogue:* OpenSLR crowdsourced Spanish read speech (wget-able, no auth).
  - *Radio music:* MUSAN music subset.
- **Mixer:** seeded/reproducible. For each clean take × interference type × SNR in
  {15, 10, 5, 0, −5} dB (RMS-based, computed over the speech-active region of the
  clean take), pick a random interference segment, scale it so the mix hits the target SNR
  against that speech-active RMS, mix, and write a JSONL manifest row (`utterance id, wav path, reference, speaker, snr, interference
  type`). Clean (no-mix) rows included as the control condition.

## Conditions

| Condition | Model | Runtime | Notes |
|-----------|-------|---------|-------|
| `raw` | — | — | Baseline |
| `gtcrn` | GTCRN streaming ONNX (official ckpt) | onnxruntime | 16 kHz native; the cheap hub-deployable candidate |
| `dfn3` | DeepFilterNet3 | `deepfilternet` pip (torch) | 16→48→16 kHz round-trip; strong-denoiser upper bound |
| `tse` | WeSep pretrained TSE | torch | Conditioned on a speaker embedding computed from the *other* takes of the same speaker (no leakage of the utterance under test); the only family that can subtract competing speech |

Known risk: WeSep pretrained-checkpoint availability and English→Spanish transfer.
First implementation task validates the checkpoint end-to-end on one file; fallback
is USEF-TSE's released checkpoints.

## Transcription

Two swappable backends, both retained per-utterance with their confidence score:

1. **Prod parity:** patched wyoming-whisper (base model + Silero VAD) over the
   compose network, reusing the `scripts/verify-whisper-score.py` Wyoming client
   pattern. Requires the compose stack up.
2. **Lemonade preview:** faster-whisper **medium** locally — the model tier the
   AI 370 box will run, so the decision reflects the future, not just today.

## Metrics & Report

- **WER** (primary) and **CER** via `jiwer`, with Whisper-style normalization:
  casefold, strip punctuation, collapse whitespace, **keep** diacritics.
- Whisper confidence-score distributions per cell (secondary signal).
- Output: `report.md` with model × SNR tables per interference type per backend,
  plus a per-utterance CSV for drill-down.

**Decision rule:** a model earns hub-integration consideration only if it cuts WER
by ≥20 % relative in the low-SNR (≤5 dB) speech-interference cells **and** does not
regress the clean/high-SNR cells. A positive result gates on the phase-2 field
corpus before any production wiring.

## Structure & Testing

- `scripts/stt-enhancement-eval/` — uv-managed Python env; single CLI with staged
  subcommands: `fetch` (datasets + voices), `mix`, `process`, `transcribe`,
  `report`. Every stage reads/writes manifest-driven artifacts under a gitignored
  `runs/` directory and is idempotent/cached, so re-running a later stage never
  recomputes an earlier one.
- **TDD (pytest)** for the pure logic: SNR mixing math, text normalization,
  manifest handling, take→phrase mapping. Model-inference and network stages are
  verified by a one-file smoke run rather than unit tests.

## Phase 2 (outline only)

A field-recording script in the style of `enroll-voice.sh`: scripted phrases read in
fran-office with the real TV at 2–3 volumes and positions, saved with a manifest in
the same schema. The harness's `mix` stage is skipped; everything downstream runs
unchanged. Built only if round 1 shows a candidate worth the session.
