# nabu-satellite wake-word training

Trains a replacement `ok_nabu.onnx` classifier for the satellite's wake pipeline
(melspec → embedding → classifier, `[1,16,96]` f32 → `[1,1]` f32) using
[`livekit-wakeword`](https://github.com/livekit/livekit-wakeword), self-hosted on the
WSL2 NVIDIA GPU. See `docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md`
and `docs/superpowers/plans/2026-07-17-wake-word-reliability.md` for the full design and
task plan; this README is the day-to-day runbook for Tasks 4–5 onward.

**Spanish-first, VoxCPM-only.** The household says "ok nabu" Spanish-style
("o-ké ná-bu"). Multilingual wake words require `tts_backend: voxcpm` — Piper is
English-US only (confirmed in `prod.yaml`'s own header comment upstream). All three
configs here (`ok_nabu_smoke.yaml`, `ok_nabu_small.yaml`, `ok_nabu_medium.yaml`) reflect
that: `ok_nabu_small.yaml`/`ok_nabu_medium.yaml` are based on upstream's
`configs/prod_voxcpm.yaml` (pinned commit `60b5d755`), with Spanish `target_phrases`,
Spanish `custom_negative_phrases`, and Castilian-Spanish `voice_design_prompts`.
`ok_nabu_smoke.yaml` is the cheap Piper-based sanity config from the toolchain spike
(Task 2) — keep it around for fast end-to-end pipeline checks; it is not
Spanish-representative and is never a training candidate.

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

**Before running `ok_nabu_small.yaml` / `ok_nabu_medium.yaml` (VoxCPM backend), the
`voxcpm` extra is required** — it was NOT installed during the toolchain spike (only
Piper-scale smoke data was fetched then) and must be added first:

```bash
uv pip install 'livekit-wakeword[train,eval,export,voxcpm]'
# or, per prod_voxcpm.yaml's own header:
uv sync --extra train --extra voxcpm
```

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

## Run commands

All commands assume `cwd = satellite/training` with the venv activated — every config's
`data_dir`/`output_dir` is the relative `./data` / `./output`, so cwd matters.

```bash
livekit-wakeword setup --config ok_nabu_medium.yaml   # downloads VoxCPM2 weights, MUSAN, RIRs, ACAV cache
livekit-wakeword run ok_nabu_small.yaml                # full pipeline, small model
livekit-wakeword run ok_nabu_medium.yaml               # full pipeline, medium model
```

`setup` only needs to run once (configs share `data_dir`); `run ok_nabu_small.yaml` and
`ok_nabu_medium.yaml` each regenerate their own `output/<model_name>/`. Despite
`--help` describing the pipeline as "generate → augment → train → export" (4 steps),
`run` actually executes **6 steps** — generate, augment, extract features, train,
export, **evaluate** — and step 6 writes `*_eval.json` / `*_det.png` into
`output/<model_name>/`. Budget accordingly; full-scale (`n_samples: 25000`,
`steps: 100000`) is expected to take **hours**, not the ~70s the smoke config took.

### Before the full run: generate a sample and listen

The `target_phrases` in both real configs are **provisional** — hand-written
Spanish spellings of "ok nabu" nobody has heard synthesized yet (VoxCPM2 wasn't
downloaded during Tasks 1–2, only the Piper smoke datasets were). Before committing
GPU-hours to the full run:

```bash
cp ok_nabu_medium.yaml /tmp/ok_nabu_listen.yaml
# edit /tmp/ok_nabu_listen.yaml: n_samples: 15 (leave everything else)
livekit-wakeword generate /tmp/ok_nabu_listen.yaml
# find the generated positive clips (under output/ or data/, per that run's model_name)
paplay <path-to-a-generated-positive.wav>   # WSLg audio out
```

Listen to a sample from each `target_phrases` spelling variant. Keep the ones that
sound like the household's "o-ké ná-bu"; drop or replace ones that don't. Also confirm
the `voice_design_prompts` personas sound like plausible Castilian Spanish speakers —
if VoxCPM is leaning into an accent that doesn't fit, adjust the prompt wording. Fix
`target_phrases` (and personas, if needed) in **both** `ok_nabu_small.yaml` and
`ok_nabu_medium.yaml` before running the full pipeline, then delete the scratch config.

### Deferred lever: English-Piper phonetic-variant spellings

An earlier design option was a *second* generation pass using the Piper backend with
English-phonetic-hack spellings of "ok nabu" (e.g. "okay nabboo", "oh keh nah boo") to
widen pronunciation-spelling coverage the way upstream's own English examples do. This
is **deferred, not built** — VoxCPM synthesizes native Spanish directly, so it isn't
needed up front. **If Task 5's eval shows low recall or the VoxCPM voice diversity looks
insufficient**, revisit this: add a `piper_vits` generate pass with phonetic-variant
`target_phrases` into a separate `data_dir`, then merge the resulting positive clips in
before `augment` (the pipeline stages are file-based, so `cp`-ing WAVs together between
a `generate`-only run and the real one is safe — verify with a tiny `n_samples` first
that a second `generate` doesn't wipe the first backend's clips).

## Mandatory post-export step: onnxsim on every export

**Every ONNX this pipeline exports — `dnn` or `conv_attention`, smoke/small/medium —
must be run through `onnxsim` before it will load in the satellite.** Both
architectures fail tract 0.23's optimizer on the raw export: `conv_attention` fails on
`PermuteAxes` (from the decomposed `nn.MultiheadAttention`), `dnn` fails on `Reshape`
(the dynamic `batch` dimension in `embeddings: ['batch', 16, 96]`). Both pass after
simplifying with the batch dimension pinned to 1:

```bash
uvx --from onnxsim --with onnxruntime onnxsim \
  output/<model_name>/<model_name>.onnx \
  output/<model_name>/<model_name>.onnx \
  --overwrite-input-shape embeddings:1,16,96
```

Use `--with onnxruntime` — the bare `uvx --from onnxsim onnxsim ...` form fails in this
environment: `onnxsim` lazily checks for `onnxruntime` and, if missing, tries to
self-install via `pip`, but `uv`-managed ephemeral tool envs don't ship `pip`, so the
fallback itself errors out (`ModuleNotFoundError: No module named 'pip'`).

The gate test is `satellite/tests/tract_clf_compat.rs` — it globs every `.onnx` file
under `satellite/tests/fixtures/clf/` and asserts each loads, optimizes, and runs under
the satellite's exact `[1,16,96] → [1,1]` contract. Compat-check a candidate by copying
it into that directory and running `cargo test --test tract_clf_compat -- --nocapture`
from `satellite/`; remove it again afterward (only the committed `hey_livekit.onnx`
fixture stays).

### Gate decision (Task 1)

`model.model_type: conv_attention` is used in `ok_nabu_small.yaml` /
`ok_nabu_medium.yaml` because Task 1's spike proved it loads and runs under tract 0.23
— **after** onnxsim simplification (raw export fails on `PermuteAxes`, as above). The
`dnn` fallback was not needed. This decision applies to every config in this directory;
do not switch back to `dnn` without re-running the Task 1 gate test and updating the
spec's Phase 0 section.

## Recordings protocol (Task 4)

Real household "ok nabu" recordings get merged into the training positives (Task 5) and
used as holdout evaluation clips (Tasks 8–9). Capture happens on the fran-office Pi
(`192.168.5.11`), the production mic path.

**The satellite service holds the mic exclusively — stop it before any manual
`arecord`, and restart it immediately after. Leave `nabu-micclock` running throughout**
(the XVF3800 capture engine needs its clock feeder; tearing it down is a separate,
unrelated failure mode).

```bash
ssh <user>@192.168.5.11
sudo systemctl stop nabu-satellite          # frees the exclusive plughw mic; leave nabu-micclock RUNNING
arecord -l                                   # note the XVF3800 card NAME (same card the service's mic command uses)
# Per take — speaker says "ok nabu" naturally, phrase ENDING near the clip end:
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
│   ├── train/               # gitignored — real household positives merged into training (Task 5)
│   └── holdout/              # gitignored — real household positives held out for eval/calibration (Tasks 8–9)
├── results/                 # COMMITTED (Task 5+) — eval metrics.json / DET png / acceptance.md per model, no raw model weights
├── ok_nabu_smoke.yaml       # committed — cheap Piper sanity config (toolchain spike, Task 2)
├── ok_nabu_small.yaml       # committed — real Spanish/VoxCPM config, model_size: small
├── ok_nabu_medium.yaml      # committed — real Spanish/VoxCPM config, model_size: medium
└── README.md
```

`.venv/`, `data/`, `output/`, and `recordings/` are gitignored — datasets, checkpoints,
the venv, and household audio never enter git. `results/` (created starting Task 5) IS
committed: eval metrics, DET curves, and the acceptance table, but never the trained
`.onnx`/`.pt` weights themselves (the winning model ships separately in Task 8).

## Known caveats

- **MUSAN HuggingFace 429s.** During the toolchain spike's `setup`, HuggingFace
  rate-limited the MUSAN background-noise download partway through (`429 Too Many
  Requests`), landing 728/774 files. The pipeline ran fine on the partial set at smoke
  scale. For the full-scale runs, if the same 429 recurs and leaves a bigger gap, either
  set an `HF_TOKEN` env var (the download log suggests this for higher rate limits) or
  re-run `livekit-wakeword setup` once more to top up the missing files before trusting
  `background_paths` coverage.
- **`ok_nabu_small.yaml` / `ok_nabu_medium.yaml` differ in exactly two lines** —
  `model_name` and `model.model_size` — verified with `diff ok_nabu_small.yaml
  ok_nabu_medium.yaml`. Every other value (RIR/MUSAN augmentation, `rounds: 3`,
  `steps: 100000`, `max_negative_weight: 3000`, `target_fp_per_hour: 0.1`, ACAV batch
  settings) is copied verbatim from upstream's `prod_voxcpm.yaml` and must stay that
  way — if you need to change a shared value, change it in both files identically.
- **`tts_batch_size: 50`** is a Piper-only knob (VoxCPM synthesizes sequentially and
  ignores it); left at upstream's value rather than tuned for VRAM.
