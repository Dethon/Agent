# nabu-satellite wake-word training

Trains a replacement `ok_nabu.onnx` classifier for the satellite's wake pipeline
(melspec → embedding → classifier, `[1,16,96]` f32 → `[1,1]` f32) using
[`livekit-wakeword`](https://github.com/livekit/livekit-wakeword), self-hosted on the
WSL2 NVIDIA GPU. See `docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md`
and `docs/superpowers/plans/2026-07-17-wake-word-reliability.md` for the full design and
task plan; this README is the day-to-day runbook for Tasks 4–5 onward.

**Spanish/English two-pass, VoxCPM-only.** The household says "okey nabu"
("o-KEY NA-bu" — keep the /y/, no internal pause). Multilingual wake words require
`tts_backend: voxcpm` — Piper is English-US only (confirmed in `prod.yaml`'s own header
comment upstream).

## Two-pass accent-matched design

VoxCPM synthesis needs **accent-matched spellings**: Spanish voices read Spanish
spellings correctly ("okey nabu", "okeynabu", "okei nabu"), English voices read
English-native spellings correctly ("okay naboo", "okay nahboo") — mixing them mangles
the phrase (an English persona reading a Spanish spelling, or vice versa, comes out
wrong). This isn't just a phrasing preference: `livekit-wakeword`'s VoxCPM backend pairs
clip index `i` with `target_phrases[i % nPhrases]` and the `(persona, cfg, steps)`
diversification triple with `flat[i % (nPersonas*nCfg*nSteps)]` **independently**
(`data/tts/voxcpm_backend.py`, `synthesize_clips` / `diversification_triple_at_index`) —
there's no config knob to pin "this persona only ever reads this phrase pool" within a
single `generate` call.

The fix is **two separate `generate` invocations sharing the same `model_name`** (hence
the same `output/ok_nabu/` split directories):

- **`ok_nabu_es.yaml`** — Spanish pass. `target_phrases` are Spanish spellings,
  `voxcpm_tts.voice_design_prompts` are Castilian-Spanish personas. `n_samples` /
  `n_samples_val` / `n_background_samples` / `n_background_samples_val` are **half** the
  full totals (12500/2500/1000/250) — this pass fills indices `0..n-1` of each split.
- **`ok_nabu_en.yaml`** — English pass. `target_phrases` are English-native spellings,
  personas are English. The sample counts are the **full** totals
  (25000/5000/2000/500) — `generate` against this file counts each split directory's
  existing clips and **resumes** from there (`start_index = existing count`, see
  `run_generate` in `data/generate.py`), appending English-phrase/English-persona clips
  up to the full totals. Every clip ends up synthesized by a persona that can actually
  pronounce the phrase text it was paired with.

Validated phrase lists (Task 5 listening pass):

| Pass | `target_phrases` |
|------|-------------------|
| Spanish (`ok_nabu_es.yaml`) | `okey nabu`, `okey-nabu`, `okeynabu`, `okei nabu` |
| English (`ok_nabu_en.yaml`) | `okay naboo`, `okay nabu`, `okey naboo`, `okay nahboo` |

**Fast/fluid pacing only.** The listening pass found that slow-pacing persona wording
("ritmo pausado/lento/mesurado" in Spanish; "slow/measured/deliberate/slower" in
English) caused VoxCPM to render a long internal pause inside the phrase, breaking the
wake word into two disconnected words — the root-cause bug behind the earlier
single-pass config. Every persona in both configs is written with brisk/fluid/
conversational pacing only; **do not reintroduce slow-pacing wording** when editing
`voice_design_prompts`.

`ok_nabu_smoke.yaml` is the cheap Piper-based sanity config from the toolchain spike
(Task 2) — keep it around for fast end-to-end pipeline checks; it is not
Spanish/English-representative and is never a training candidate.

## Install

### System packages (apt, sudo required)

```bash
sudo apt-get install -y espeak-ng libsndfile1 ffmpeg sox portaudio19-dev
```

### `uv` + Python 3.11 venv

`uv`/`uvx` are not preinstalled; install them user-space (no root needed). The system
Python is 3.10, but `livekit-wakeword` requires `>=3.11` — let `uv` fetch its own
interpreter rather than using the system one:

```bash
pipx install uv                                # -> uv 0.11.29, uvx 0.11.29, ~/.local/bin
cd satellite/training
uv venv --python 3.11 .venv                    # uv downloads cpython-3.11.15 itself
source .venv/bin/activate
```

### `livekit-wakeword` package

**Pinned version: `livekit-wakeword==0.2.1`.**

For the smoke config only (Piper backend, no voice-design TTS), the toolchain spike used:

```bash
uv pip install 'livekit-wakeword[train,eval,export]'
```

**Before running `ok_nabu_es.yaml` / `ok_nabu_en.yaml` (VoxCPM backend), the `voxcpm`
extra is required:**

```bash
uv pip install 'livekit-wakeword[train,eval,export,voxcpm]'
# or, per prod_voxcpm.yaml's own header:
uv sync --extra train --extra voxcpm
```

`voxcpm==2.0.3` is now installed in `.venv`.

Verify install + GPU:

```bash
python -c "import importlib.metadata as m; print(m.version('livekit-wakeword'))"
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
# expected: 0.2.1 / True <your GPU> (toolchain spike measured True NVIDIA GeForce RTX 4090)
```

### Reference clone (config templates only, never edited)

```bash
git clone --depth 1 https://github.com/livekit/livekit-wakeword /tmp/lkww-ref   # commit 60b5d755
```

## Runbook

All commands assume `cwd = satellite/training` with the venv activated — every config's
`data_dir`/`output_dir` is the relative `./data` / `./output`, so cwd matters. Verified
stage subcommands (`livekit-wakeword --help` / `livekit-wakeword <stage> --help` in this
venv, `livekit-wakeword==0.2.1`): **`setup`, `generate`, `augment`, `train`, `export`,
`eval`, `run`** — there is **no separate `extraction` subcommand**; `augment` runs both
augmentation *and* feature extraction internally (`run_augment` then `run_extraction`,
see `cli.py`) as one CLI stage. Run each stage individually below — **never `run`** (the
one-shot `generate → augment → train → export → eval` pipeline), because the two-pass
design needs two separate `generate` invocations before augment ever runs.

### (a) `setup` — download shared dependencies

```bash
livekit-wakeword setup --config ok_nabu_es.yaml
```

Downloads/caches (shared `data_dir` across both configs, so this is a one-time step):
VoxCPM2 weights, ACAV100M + validation features, MIT RIRs, MUSAN background noise. All
four are already fully cached in this environment (VoxCPM2 snapshot at
`data/voxcpm/VoxCPM2/`, ACAV100M+validation `.npy` under `data/features/`, 270 RIR wavs
under `data/rirs/`, **MUSAN at 774/774** wavs under `data/backgrounds/`). `setup`
short-circuits per-artifact once files exist — the VoxCPM check is
`dest.is_dir() and any(dest.iterdir())`, the MUSAN check is "any `**/*.wav` already
present" (`cli.py`) — so re-running it here won't re-download anything; it's safe to run
again as a no-op sanity check, or to top up if a download was ever interrupted.

### (b) `generate` — Spanish pass (indices `0..12499`)

```bash
livekit-wakeword generate ok_nabu_es.yaml
```

Fills `output/ok_nabu/{positive,negative,background}_{train,test}/` with the first half
of each split: `positive_train` clips `clip_000000.wav..clip_012499.wav` (Spanish
phrases × Castilian-Spanish personas), `positive_test` `clip_000000.wav..clip_002499.wav`,
proportionally for `negative_*`/`background_*`.

### (c) `generate` — English pass, RESUMES indices `12500..24999`

```bash
livekit-wakeword generate ok_nabu_en.yaml
```

Same `model_name: ok_nabu` → same `output/ok_nabu/` split directories. `run_generate`
counts existing `clip_######.wav` files in each split dir and calls
`synthesize_clips(..., start_index=existing)`; since `ok_nabu_en.yaml`'s `n_samples` /
etc. are the *full* totals (25000/5000/2000/500), this pass sees the Spanish pass's
partial counts (12500/2500/1000/250), resumes from exactly those indices, and appends
English-phrase/English-persona clips up to the full totals
(`clip_012500.wav..clip_024999.wav` for `positive_train`, etc.) — merging the two
accent-matched pools into one dataset. Negative and background splits resume/append the
same way with English-pass negatives and phrase-independent background noise
respectively.

### (d) inject real household recordings

Merge the real "okey nabu" recordings (Task 4 capture — see "Recordings protocol"
below) into `output/ok_nabu/positive_train/` **before** augmenting, so real audio gets
the same augmentation/RIR/noise treatment as the synthetic clips.

```bash
cd satellite/training/recordings/train
n=25000   # first free index after the English pass (0..24999 already used)
for f in *.wav; do
  cp "$f" "../../output/ok_nabu/positive_train/clip_$(printf '%06d' "$n").wav"
  n=$((n + 1))
done
```

**Filename matters**: augmentation round 0 only picks up files matching
`^clip_\d{6}\.wav$` (`data/augment.py`, `_augment_directory`'s `_src_re` for
`round_idx == 0`) — recordings dropped in under any other name are silently skipped by
`augment`. Start numbering at **25000** (one past the English pass's last index,
`24999`) so recordings never collide with a synthetic clip's filename. If `generate` is
ever re-run afterward, the split's existing-clip count will include the injected
recordings — harmless (both configs' totals are already fully met at that point, so
`generate` just skips as "already complete"), but don't rely on exact index math holding
across a re-run.

Recordings under `recordings/holdout/` are **not** injected here — they stay a separate
real-audio holdout set for evaluation/calibration (Tasks 8–9).

### (e) `augment` — augmentation + feature extraction (one CLI stage)

```bash
livekit-wakeword augment ok_nabu_en.yaml
```

Run this **once**, after both generate passes and the recordings injection. It (1) wipes
any stale `clip_*_rN.wav` files from a previous augmentation run, (2) runs 3 rounds
(`augmentation.rounds: 3`) of per-sample augmentation + RIR convolution + MUSAN
background mixing over every `clip_######.wav` in each split (synthetic clips from both
passes plus the injected recordings), and (3) immediately extracts mel→embedding
features from the resulting `clip_*_rN.wav` files into `positive_features_train.npy` /
`negative_features_train.npy` / etc. under `output/ok_nabu/`. Either config works here
(`ok_nabu_es.yaml` and `ok_nabu_en.yaml` share every value below `target_phrases`/
`custom_negative_phrases`/`voxcpm_tts.voice_design_prompts`/sample counts) — use
`ok_nabu_en.yaml` for consistency with the remaining stages.

### (f) `train`

```bash
livekit-wakeword train ok_nabu_en.yaml
```

3-phase adaptive training over the extracted features (`output/ok_nabu/*.npy`) plus
`data/features/openwakeword_features_ACAV100M_2000_hrs_16bit.npy` as the general-negative
pool. `model.model_size: medium` (see "Training medium first" below for adding a
`small` variant later). Saves `output/ok_nabu/ok_nabu.pt` + `ok_nabu_metrics.json`.

### (g) `export`

```bash
livekit-wakeword export ok_nabu_en.yaml
```

Writes `output/ok_nabu/ok_nabu.onnx` (`--quantize` is available for an INT8 embedded
export; not used for the primary candidate).

### (h) MANDATORY: `onnxsim` on every export

```bash
uvx --from onnxsim --with onnxruntime onnxsim \
  output/ok_nabu/ok_nabu.onnx \
  output/ok_nabu/ok_nabu.onnx \
  --overwrite-input-shape embeddings:1,16,96
```

**Both `conv_attention` and `dnn` need this — tract 0.23 rejects the raw dynamic-batch
export.** `conv_attention` fails on `PermuteAxes` (from the decomposed
`nn.MultiheadAttention`), `dnn` fails on `Reshape` (the dynamic `batch` dimension in
`embeddings: ['batch', 16, 96]`). Both pass after simplifying with the batch dimension
pinned to 1. Use `--with onnxruntime` — the bare `uvx --from onnxsim onnxsim ...` form
fails in this environment: `onnxsim` lazily checks for `onnxruntime` and, if missing,
tries to self-install via `pip`, but `uv`-managed ephemeral tool envs don't ship `pip`,
so the fallback itself errors out (`ModuleNotFoundError: No module named 'pip'`).

The gate test is `satellite/tests/tract_clf_compat.rs` — it globs every `.onnx` file
under `satellite/tests/fixtures/clf/` and asserts each loads, optimizes, and runs under
the satellite's exact `[1,16,96] → [1,1]` contract. Compat-check a candidate by copying
it into that directory and running `cargo test --test tract_clf_compat -- --nocapture`
from `satellite/`; remove it again afterward (only the committed `hey_livekit.onnx`
fixture stays).

### (i) `eval`

```bash
livekit-wakeword eval ok_nabu_en.yaml
```

DET curve / AUT / FPPH / recall against the held-out synthetic validation split
(`--model`/`-m` overrides the default `output/ok_nabu/ok_nabu.onnx` path if evaluating a
different checkpoint, e.g. after onnxsim or quantization). Writes
`output/ok_nabu/ok_nabu_eval.json` and `ok_nabu_det.png`.

## Training medium first; adding a `small` model later

Train **medium** first (`model.model_size: medium`, both configs above). To add a
`small` model later **without repeating `generate`/`augment`/feature-extraction**
(shared and expensive — the extracted `.npy` features under `output/ok_nabu/` don't
depend on `model.model_size` at all; only `train`/`export`/`eval` read `model:`):

There's no `--model-size` CLI override (`train --help` / `export --help` take only
`config_path` and, for `export`, `--quantize`) — the lever is a config file. `train` /
`export` key their output paths **only by `model_name`**
(`config.model_output_dir / f"{config.model_name}.pt"` /
`f"{config.model_name}.onnx"`, `config.py`'s `model_output_dir` property), so re-running
with a different `model.model_size` under the *same* `model_name: ok_nabu` will
overwrite the medium checkpoint/export/metrics. **Back up the medium artifacts first**,
then swap `model_size`:

```bash
# after (f)-(i) above have produced the medium model:
mkdir -p output/ok_nabu/medium
cp output/ok_nabu/ok_nabu.{pt,onnx,onnx.data} output/ok_nabu/medium/ 2>/dev/null
cp output/ok_nabu/ok_nabu_metrics.json output/ok_nabu/ok_nabu_eval.json output/ok_nabu/ok_nabu_det.png output/ok_nabu/medium/ 2>/dev/null

cp ok_nabu_en.yaml ok_nabu_en_small.yaml   # scratch, not committed
sed -i 's/model_size: medium/model_size: small/' ok_nabu_en_small.yaml
livekit-wakeword train ok_nabu_en_small.yaml    # reads the SAME output/ok_nabu/*.npy features
livekit-wakeword export ok_nabu_en_small.yaml
# onnxsim (step h) again — required for the small export too
uvx --from onnxsim --with onnxruntime onnxsim \
  output/ok_nabu/ok_nabu.onnx output/ok_nabu/ok_nabu.onnx \
  --overwrite-input-shape embeddings:1,16,96
livekit-wakeword eval ok_nabu_en_small.yaml
mkdir -p output/ok_nabu/small
cp output/ok_nabu/ok_nabu.{pt,onnx,onnx.data} output/ok_nabu/small/ 2>/dev/null
cp output/ok_nabu/ok_nabu_metrics.json output/ok_nabu/ok_nabu_eval.json output/ok_nabu/ok_nabu_det.png output/ok_nabu/small/ 2>/dev/null
rm ok_nabu_en_small.yaml
```

`ok_nabu_en_small.yaml` is a throwaway scratch config (like the old `ok_nabu_listen*.yaml`
listening-pass configs) — don't commit it; if a `small` model becomes a real ongoing
candidate, promote it to a committed config instead of a `sed`-patched scratch copy.

## Recordings protocol (Task 4)

Real household "okey nabu" recordings get merged into the training positives (step (d)
above) and used as holdout evaluation clips (Tasks 8–9). Capture happens on the
fran-office Pi (`192.168.5.11`), the production mic path.

**The satellite service holds the mic exclusively — stop it before any manual
`arecord`, and restart it immediately after. Leave `nabu-micclock` running throughout**
(the XVF3800 capture engine needs its clock feeder; tearing it down is a separate,
unrelated failure mode).

```bash
ssh <user>@192.168.5.11
sudo systemctl stop nabu-satellite          # frees the exclusive plughw mic; leave nabu-micclock RUNNING
arecord -l                                   # note the XVF3800 card NAME (same card the service's mic command uses)
# Per take — speaker says "okey nabu" naturally, phrase ENDING near the clip end:
arecord -D plughw:CARD=<name>,DEV=0 -r 16000 -c 1 -f S16_LE -d 3 /tmp/rec/<speaker>_<nn>.wav
sudo systemctl start nabu-satellite          # IMMEDIATELY after the session
```

Target: 30–40 takes per household speaker, varied: normal/far (2–4 m), quiet/soft
voice, a few with TV on. `scp` the takes to `satellite/training/recordings/`.

Verify format, then split per speaker (~75/25, most takes to `train/`, every 4th take
to `holdout/`; holdout needs at least 2 clips per speaker including one far-field take):

```bash
cd satellite/training/recordings
for f in */*.wav; do soxi "$f" | grep -E 'Sample Rate|Channels|Precision'; done  # all 16000 / 1 / 16-bit
```

## Data layout

```
satellite/training/
├── .venv/                  # gitignored — Python 3.11 venv
├── data/                   # gitignored — VoxCPM2 weights, MUSAN, RIRs, ACAV cache (shared across configs)
├── output/                 # gitignored — per-model_name generated clips, features, checkpoints, ONNX, eval JSON/PNG
├── recordings/
│   ├── train/               # gitignored — real household positives merged into positive_train (step (d))
│   └── holdout/              # gitignored — real household positives held out for eval/calibration (Tasks 8–9)
├── results/                 # COMMITTED (Task 5+) — eval metrics.json / DET png / acceptance.md per model, no raw model weights
├── ok_nabu_smoke.yaml       # committed — cheap Piper sanity config (toolchain spike, Task 2)
├── ok_nabu_es.yaml          # committed — Spanish pass, model_size: medium, model_name: ok_nabu
├── ok_nabu_en.yaml          # committed — English pass, model_size: medium, model_name: ok_nabu (SAME as ok_nabu_es.yaml)
└── README.md
```

`.venv/`, `data/`, `output/`, and `recordings/` are gitignored — datasets, checkpoints,
the venv, and household audio never enter git. `results/` (created starting Task 5) IS
committed: eval metrics, DET curves, and the acceptance table, but never the trained
`.onnx`/`.pt` weights themselves (the winning model ships separately in Task 8).

## Known caveats

- **`ok_nabu_es.yaml` / `ok_nabu_en.yaml` share `model_name: ok_nabu` by design** — that's
  the mechanism that merges their two `generate` passes into one dataset (see "Two-pass
  accent-matched design" above). Every value below `target_phrases`/
  `custom_negative_phrases`/`voxcpm_tts.voice_design_prompts`/the four sample-count
  fields is identical between the two files (RIR/MUSAN augmentation, `rounds: 3`,
  `steps: 100000`, `max_negative_weight: 3000`, `target_fp_per_hour: 0.1`, ACAV batch
  settings) and must stay that way — if you need to change a shared value, change it in
  both files identically.
- **MUSAN HuggingFace 429s.** During the toolchain spike's `setup`, HuggingFace
  rate-limited the MUSAN background-noise download partway through (`429 Too Many
  Requests`), initially landing 728/774 files; a re-run of `setup` topped it up to the
  full 774/774 now cached. If a future `setup` re-run (e.g. on a fresh machine) hits the
  same 429 and leaves a gap, either set an `HF_TOKEN` env var (the download log suggests
  this for higher rate limits) or re-run `livekit-wakeword setup` once more.
- **`tts_batch_size: 50`** is a Piper-only knob (VoxCPM synthesizes sequentially and
  ignores it); left at upstream's value rather than tuned for VRAM.

## Gate decision (Task 1)

`model.model_type: conv_attention` is used in both training configs because Task 1's
spike proved it loads and runs under tract 0.23 — **after** onnxsim simplification (raw
export fails on `PermuteAxes`, as above). The `dnn` fallback was not needed. This
decision applies to every config in this directory; do not switch back to `dnn` without
re-running the Task 1 gate test and updating the spec's Phase 0 section.
