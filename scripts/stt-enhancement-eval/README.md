# stt-enhancement-eval

Offline harness that measures whether speech enhancement (denoising or target-speaker
extraction) lowers Whisper WER on noisy Spanish voice commands — the phase-1 question of the
[STT enhancement design spec](../../docs/superpowers/specs/2026-07-22-stt-enhancement-eval-design.md).
It builds a synthetic corpus (enrollment voices × {clean, competing-speech, music} × an SNR grid),
runs each utterance through candidate enhancers, transcribes with two Whisper backends, and scores
WER against a fixed decision rule. No hub/production code is touched.

uv-managed Python env. Every stage reads/writes manifest-driven artifacts under a gitignored
`runs/` dir and is idempotent, so re-running a later stage never recomputes an earlier one.

```bash
uv run python -m stt_eval fetch                                             # voices + SLR73 speech + MUSAN music + gtcrn onnx
uv run python -m stt_eval validate                                         # then inspect takes.jsonl (drops clean-WER > 0.3)
uv run python -m stt_eval mix                                              # build the corpus + manifest.jsonl
uv run python -m stt_eval process --conditions gtcrn,dfn3,tse             # tse needs data/models/tse-env (conditions/tse_env_setup.sh)
uv run python -m stt_eval transcribe --backend medium   --conditions raw,gtcrn,dfn3,tse
uv run python -m stt_eval transcribe --backend lemonade --conditions raw,gtcrn,dfn3,tse   # prod parity; needs the `lemonade` compose service up
uv run python -m stt_eval report                                          # WER tables + decision block + per_utterance.csv
```

> `fetch`'s idempotence is presence-based (a source counts as done once its `data/...`
> subdirectory exists and is non-empty): if a fetch is interrupted partway, delete the affected
> `data/...` subdirectory before re-running, or the partial download is silently treated as complete.

> `mix` refuses to re-mix a run that already has a corpus: noise beds depend on every take
> iterated before a given id, so a re-mix (new take, changed exclusions, another seed) rewrites
> wavs whose ids are unchanged, and the path-keyed `process`/`transcribe` resume caches would keep
> serving stale outputs for them. `mix --force` drops corpus/processed/transcripts and rebuilds.

Committed results live in `results/` (e.g. `results/2026-07-round1.md`); the raw `runs/` artifacts
they annotate are gitignored.
