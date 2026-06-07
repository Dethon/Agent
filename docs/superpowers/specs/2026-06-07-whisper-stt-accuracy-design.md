# Whisper STT Accuracy (base model) — Design Spec

**Date**: 2026-06-07
**Status**: Approved for planning
**Owner**: Francisco Crespo
**Branch**: voice-impl

## Goal

Improve Whisper speech-to-text recognition quality for the voice channel **without changing the model size** — it stays on `base`, CPU, `int8_float32`. Improvements must be **generic** (not tailored to a specific model or language) and **non-blocking** (no transcript is ever dropped or rewritten). The raw transcript feeds a downstream LLM agent that tolerates and works around recognition errors, so the strategy is to improve raw transcription at the source and let the agent absorb the residual.

## Background — what was investigated

A research workflow probed the **actual** `rhasspy/wyoming-whisper:latest` image (sha `9501d2659ee`, wyoming-faster-whisper `3.1.0`, faster-whisper `1.2.1`, ctranslate2 `4.6.3`). Ground-truth findings that shape this design:

- The wyoming handler forwards **exactly** `{beam_size, language, initial_prompt, vad_filter, vad_parameters}` to `faster_whisper` `transcribe()`. Everything else is unreachable through this image: `no_speech_threshold`, `condition_on_previous_text`, `temperature`, `hotwords`, `compression_ratio_threshold`, `hallucination_silence_threshold`.
- `initial_prompt` and `beam_size` are **startup-only and global**; only `language` can vary per request via the Wyoming `Transcribe` event.
- **`--vad-filter` (Silero VAD) IS available and is currently OFF.** It is the single highest-value server-side lever and the only server-side hallucination defense this image offers.
- The C# `ConfidenceThreshold=0.4` gate (`TranscriptDispatcher.cs:21`) is **inert** — the server emits no `score`, so `Confidence` is always `null`. This design does not rely on it.
- There is **no hub-side onset clipping**: `UtteranceCapture.Feed` (`UtteranceCapture.cs:26-30`) forwards every frame; `SilenceGate` only decides *when the turn ends*. Any real onset clipping is satellite-side (Pi wake endpointing), outside this repo.

## Constraints and choices

- **Model**: stays `base`, CPU, `int8_float32`. No model-size change.
- **Latency**: some extra per-utterance latency is acceptable.
- **Generic only**: no model-specific or language-specific post-processing (this ruled out a Spanish-cities fuzzy proper-noun corrector and a Whisper-specific hallucination phrase blocklist).
- **Non-blocking**: never drop or rewrite a transcript. The pre-existing empty-string drop in `TranscriptDispatcher` stays as-is (an empty string has nothing for the agent to act on); B adds **no new gating**.
- **Segmentation stays ON** (user choice). VAD composes with it: with segmentation enabled, VAD trims non-speech *within* each segment, which de-risks segmentation's main downside (each segment is internally padded to 30s, breeding silence-hallucinations).

## Scope — the changes

### 1. Enable and verify Silero VAD — `DockerCompose/docker-compose.yml`

Add `--vad-filter` to the `wyoming-whisper` `command` list:

```yaml
      - --vad-filter
```

- Start with **default** VAD parameters. The image exposes three tunable knobs if needed: `--vad-threshold` (default `0.5`), `--vad-min-silence-ms` (default `2000`), `--vad-min-speech-ms` (default `250`). `speech_pad_ms` is **not** exposed (uses the faster-whisper default).
- **Verification gate (empirical):** confirm VAD does not clip quiet word onsets. If it does, lower `--vad-threshold` (try `0.4`, then `0.3`) and re-test. Record the final value in the commit message.
- **Mechanism:** Silero VAD removes non-speech before the decoder, so the model never invents text on silence/noise — addressing the hallucination failure mode at the source rather than by blocking output.

### 2. Trim the `initial-prompt` — `DockerCompose/docker-compose.yml`

The current prompt is a ~390-char prose paragraph that exceeds the ~224-token (~150–250 char) budget; faster-whisper keeps only the last 224 tokens and weights the tail most. Replace it with a short, frequency-ranked, accented string ending with the deployment Locality. **Proposed starting value** (tunable):

```
Asistente de voz en español de España (castellano): domótica, recordatorios, listas de la compra, el tiempo y preguntas generales. Nombres propios y ciudades españolas, p. ej. Valladolid.
```

- This is the **only** proper-noun lever in B — a soft assist, not a fix.
- **Caveat:** prompt biasing slightly raises silence-hallucination odds (the decoder continues the prompt's style on silence); VAD (item 1) offsets this.

### 3. Config hygiene

- **Remove the no-op per-request model `name` send.** Delete `WyomingSpeechToText.cs:28-31`:
  ```csharp
  if (config.Model is not null)
  {
      transcribeData["name"] = config.Model;
  }
  ```
  The server ignores the per-request model name (only startup `--model` decides). Verified: `config.Model` is referenced **only** here.
- **Remove the now-unused config.** Drop the `Model` property from `WyomingSttConfig` (`SttSettings.cs:13`) and the `"Model": "base"` entry from `appsettings.json:23` — both only fed the no-op send. (The compose `--model base` is untouched; that is the real model selector.)
- **Fix stale comments** in `docker-compose.yml` (the "'medium' for accurate Castilian…" and "int8 keeps 'medium' fast…" comments) to reflect the running config: `base` + Silero VAD.

### 4. (Optional, included) `--local-files-only` — `DockerCompose/docker-compose.yml`

Add `--local-files-only` to skip HuggingFace hub update checks on every start (faster, deterministic boots, robust to the known WARP/WSL egress issues). **Only after** confirming the `base` model is present in `./volumes/whisper-data`, otherwise first boot can't download it. Operational hardening, unrelated to accuracy.

## Out of scope / deferred

- **Proper-noun fuzzy corrector** — rejected (non-generic, weak vocab, over-correction risk).
- **Hallucination blocklist / any transcript blocking** — rejected (blocking is lossy/brittle and the list is model-specific; the agent tolerates residual junk; VAD handles the root cause).
- **Pre-roll buffer** — invalid; no hub-side onset clipping exists.
- **Disabling segmentation** — user keeps decode-ahead enabled.
- **Approach C (deferred, revisit only if base + VAD prove insufficient after measurement):** a custom/patched image to reach `condition_on_previous_text=False` / `temperature=0` / `no_speech_threshold` and to emit `avg_logprob` (reviving the confidence gate); or `speaches` + `wyoming_openai` for per-request `hotwords`/prompt. This is the principled route for proper nouns and residual hallucinations.
- **Satellite-side onset tuning** — `wyoming-satellite` wake endpointing on the Pi, outside this repo.

## Testing and verification

- **TDD (C# hygiene):** in `Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs`, add a failing test (RED) asserting the emitted `transcribe` event carries **no** `name` field; then make it pass by removing the send. Existing STT/dispatcher tests stay green.
- **Build:** solution builds; `dotnet format` clean per repo hooks.
- **VAD verification (empirical, post-deploy):** speak a set of short commands including quiet onsets (e.g. "Para", "Apaga la luz") plus deliberate silent/noisy periods. Confirm (a) onsets are not clipped and (b) silence no longer yields hallucinated text. Tune `--vad-threshold` if onsets clip; record final params.
- **Infra rule:** `docker-compose.yml` is the only infra file touched and `appsettings.json` is updated in the same change. No new environment variables or secrets.

## Risks

- **VAD over-trims and clips onsets** → mitigated by the threshold-tuning verification gate; VAD is a single flag, trivially reverted.
- **Trimmed prompt loses some bias coverage** → acceptable (soft lever, tolerant agent, tunable).
- **Removing the `Model` field** → confirmed no other references; low risk.

## Rollback

Every change is config plus one small C# deletion. Revert the commit to fully restore prior behavior; VAD alone can be disabled by removing the single `--vad-filter` line.
