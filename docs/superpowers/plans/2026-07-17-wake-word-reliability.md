# Wake Word Reliability (custom "ok nabu" via livekit-wakeword) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the stock English-trained `ok_nabu.onnx` classifier with a self-trained, Spanish-first, noise/reverb-augmented livekit-wakeword model, gated by a tract-compatibility spike, and recalibrate the deployed operating point.

**Architecture:** The satellite's wake pipeline (melspec → Google embedding → classifier, all tract-onnx) stays untouched; only the classifier artifact `satellite/models/ok_nabu.onnx` is replaced (identical `[1,16,96] → [1,1]` contract). Training runs off-repo on the WSL2 NVIDIA GPU via the `livekit-wakeword` CLI; configs/results are committed under `satellite/training/`.

**Tech Stack:** Rust + tract-onnx 0.23 (existing), livekit-wakeword (Python ≥3.11, Apache-2.0), uv, sox/ffmpeg/espeak-ng, arecord on the Pi.

**Spec:** `docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md`

## Global Constraints

- Classifier ONNX contract is fixed: input `[1,16,96]` f32, output `[1,1]` f32 score — anything else is a task failure, not something to adapt the detector to.
- Reference livekit-wakeword commit for downloads/config templates: `60b5d755` (github.com/livekit/livekit-wakeword). Pin the installed package version in `satellite/training/README.md` the moment it's installed.
- Audio fixtures: 16 kHz mono S16LE WAV only (the crate asserts this).
- Cortex-A53 budget: total per-chunk wake inference (`wake inference` debug line, µs) must stay **< 40 000 µs** (half the 80 ms chunk period) with the new classifier.
- The fran-office Pi (`192.168.5.11`) is a LIVE household unit — every deploy/stop in Tasks 4/6/7/9 briefly disrupts it; batch on-Pi work, don't leave the service stopped.
- The satellite service holds the mic exclusively (`plughw`) — **stop `nabu-satellite` before any manual `arecord`, restart after** (`nabu-micclock` must stay running: the XVF3800 capture engine needs its clock feeder).
- Commit style: `type(satellite): subject` (see `git log --oneline -- satellite/`). Never switch branches; commit on the current branch.
- Large untracked artifacts (datasets, checkpoints, venvs, recordings) never enter git — `satellite/training/.gitignore` (Task 3) is authoritative.

---

### Task 1: tract compatibility spike — conv_attention load test (THE GATE)

**Files:**
- Create: `satellite/tests/tract_clf_compat.rs`
- Create: `satellite/tests/fixtures/clf/hey_livekit.onnx` (downloaded, ~953 KB, temporary — removed in Task 10)

**Interfaces:**
- Produces: the **gate decision** `model_type: conv_attention` vs `model_type: dnn`, recorded in the spec's "Phase 0" section. Every training config in Task 3 consumes this decision.
- Produces: `satellite/tests/fixtures/clf/` — a directory the test globs; later tasks drop candidate ONNX files here to compat-check them (Task 2 does).

- [ ] **Step 1: Write the failing test**

```rust
// satellite/tests/tract_clf_compat.rs
// Gate for the wake-model retrain (spec 2026-07-17-wake-word-reliability): proves tract 0.23
// can load + run every classifier candidate in tests/fixtures/clf/ under the satellite's
// exact contract ([1,16,96] f32 -> [1,1] f32). livekit-wakeword's conv_attention export is
// opset 18 with fused LayerNormalization and Shape/Gather/Slice chains from the decomposed
// nn.MultiheadAttention — exactly the ops this test probes. Temporary: removed once the
// trained model ships embedded (spike_wake.rs then covers the same ops via include_bytes!).
use tract_onnx::prelude::*;

#[test]
fn classifier_candidates_load_and_run_under_tract() {
    let dir = std::path::Path::new("tests/fixtures/clf");
    let candidates: Vec<_> = std::fs::read_dir(dir)
        .expect("tests/fixtures/clf missing")
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "onnx"))
        .collect();
    assert!(!candidates.is_empty(), "no candidate .onnx files in tests/fixtures/clf");
    for path in candidates {
        let model = tract_onnx::onnx()
            .model_for_path(&path)
            .unwrap_or_else(|e| panic!("{path:?}: parse failed: {e}"))
            .with_input_fact(0, f32::fact(&[1, 16, 96]).into())
            .unwrap()
            .into_optimized()
            .unwrap_or_else(|e| panic!("{path:?}: optimize failed: {e}"))
            .into_runnable()
            .unwrap_or_else(|e| panic!("{path:?}: runnable failed: {e}"));
        let input: Tensor = tract_ndarray::Array3::<f32>::zeros((1, 16, 96)).into();
        let out = model
            .run(tvec!(input.into()))
            .unwrap_or_else(|e| panic!("{path:?}: run failed: {e}"));
        let score = out[0].to_plain_array_view::<f32>().unwrap()[[0, 0]];
        assert!(score.is_finite(), "{path:?}: non-finite score {score}");
        println!("{}: OK, zeros-input score = {score}", path.display());
    }
}
```

- [ ] **Step 2: Run test to verify it fails (no fixture dir yet)**

Run: `cd satellite && cargo test --test tract_clf_compat`
Expected: FAIL with `tests/fixtures/clf missing`

- [ ] **Step 3: Download the shipped conv_attention example (pinned commit)**

```bash
mkdir -p satellite/tests/fixtures/clf
curl -fL -o satellite/tests/fixtures/clf/hey_livekit.onnx \
  https://raw.githubusercontent.com/livekit/livekit-wakeword/60b5d755/examples/resources/hey_livekit.onnx
ls -l satellite/tests/fixtures/clf/   # expect ~953 KB
```

- [ ] **Step 4: Run the test — this run IS the gate**

Run: `cd satellite && cargo test --test tract_clf_compat -- --nocapture`
Expected (gate-pass): PASS with `... hey_livekit.onnx: OK, zeros-input score = 0.xxx`

**If it fails** (parse/optimize/run panic naming an unsupported op):

```bash
# Retry once with onnx-simplifier, batch pinned to 1 (folds Shape/Gather chains to constants)
uvx --from onnxsim onnxsim \
  satellite/tests/fixtures/clf/hey_livekit.onnx \
  satellite/tests/fixtures/clf/hey_livekit.onnx \
  --overwrite-input-shape embeddings:1,16,96
cd satellite && cargo test --test tract_clf_compat -- --nocapture
```

Still failing → **gate decision = `dnn`**: delete `hey_livekit.onnx`, keep the test (Task 2's dnn export will populate the dir), and use `model_type: dnn` everywhere Task 3 says `conv_attention`.

- [ ] **Step 5: Record the gate outcome in the spec**

Edit `docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md`, "Phase 0" section, append: `**Gate outcome (YYYY-MM-DD):** conv_attention loads and runs under tract 0.23 [as-exported | after onnxsim batch=1 | FAILED — dnn fallback selected]`.

- [ ] **Step 6: Commit**

```bash
git add satellite/tests/tract_clf_compat.rs satellite/tests/fixtures/clf/ \
  docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md
git commit -m "test(satellite): tract compat gate for livekit-wakeword classifier heads"
```

---

### Task 2: training toolchain sanity run (test-scale, our own export path)

**Files:**
- Create: `satellite/training/.venv/` (untracked)
- Create: `/tmp/lkww-ref/` (throwaway clone for config templates)
- Create: `satellite/training/ok_nabu_smoke.yaml` (committed in Task 3 alongside the real configs; authored here)

**Interfaces:**
- Consumes: `satellite/tests/fixtures/clf/` glob dir from Task 1.
- Produces: a validated local toolchain (CUDA-in-WSL + system deps + downloads) and the exact pinned `livekit-wakeword` version string for Task 3's README.
- Produces: `satellite/training/output/ok_nabu_smoke/ok_nabu_smoke.onnx` — a conv_attention (or dnn, per gate) export from OUR pipeline, compat-checked by the Task 1 test.

- [ ] **Step 1: Install system deps + CLI, record the version**

```bash
sudo apt-get install -y espeak-ng libsndfile1 ffmpeg sox portaudio19-dev
mkdir -p satellite/training && cd satellite/training
uv venv .venv && source .venv/bin/activate
uv pip install 'livekit-wakeword[train,eval,export]'
python -c "import importlib.metadata as m; print(m.version('livekit-wakeword'))"  # RECORD THIS
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
```

Expected: version prints (record for Task 3 README); `True <your GPU>`. If CUDA is `False`, fix the WSL2 CUDA setup before proceeding — TTS generation requires it.

- [ ] **Step 2: Clone the reference repo (templates only) and author the smoke config**

```bash
git clone --depth 1 https://github.com/livekit/livekit-wakeword /tmp/lkww-ref
cp /tmp/lkww-ref/configs/test.yaml satellite/training/ok_nabu_smoke.yaml
```

Edit `satellite/training/ok_nabu_smoke.yaml`: set `model_name: ok_nabu_smoke`, `target_phrases: ["ok nabu"]`, and point its data/output dirs (whatever keys `test.yaml` uses — copy the file's own key names, do not invent) at `satellite/training/data` and `satellite/training/output`. Change nothing else — smoke scale (`n_samples: 100`, `steps: 500`, tiny) is the point.

- [ ] **Step 3: Run setup + the full smoke pipeline**

```bash
cd satellite/training && source .venv/bin/activate
livekit-wakeword setup --config ok_nabu_smoke.yaml   # downloads Piper ckpt, MUSAN, RIRs, ACAV cache
livekit-wakeword run ok_nabu_smoke.yaml              # generate -> augment -> extraction -> train -> export -> eval
ls output/ok_nabu_smoke/                             # expect ok_nabu_smoke.onnx + metrics/eval json + DET png
```

Expected: completes end-to-end (quality is irrelevant at this scale); note rough wall-clock as the scaling baseline.

- [ ] **Step 4: Compat-check OUR export through the Task 1 gate test**

```bash
cp satellite/training/output/ok_nabu_smoke/ok_nabu_smoke.onnx satellite/tests/fixtures/clf/
cd satellite && cargo test --test tract_clf_compat -- --nocapture
rm satellite/tests/fixtures/clf/ok_nabu_smoke.onnx   # smoke artifact, not committed
```

Expected: PASS for both files. If OUR export fails where `hey_livekit.onnx` passed, diff the two with `python -c "import onnx; m=onnx.load('...'); print(m.opset_import, [n.op_type for n in m.graph.node])"` and resolve (onnxsim as in Task 1) before any full-scale training.

- [ ] **Step 5: Commit** — nothing to commit yet (smoke YAML is committed with Task 3; artifacts untracked). Record the version string + wall-clock in your notes for Task 3.

---

### Task 3: committed training configs + README (`satellite/training/`)

**Files:**
- Create: `satellite/training/.gitignore`
- Create: `satellite/training/README.md`
- Create: `satellite/training/ok_nabu_small.yaml`
- Create: `satellite/training/ok_nabu_medium.yaml`
- Commit also: `satellite/training/ok_nabu_smoke.yaml` (from Task 2)

**Interfaces:**
- Consumes: gate decision (Task 1), pinned version + smoke config (Task 2), `/tmp/lkww-ref` clone.
- Produces: `ok_nabu_small.yaml` / `ok_nabu_medium.yaml` — the exact configs Task 5 runs verbatim; `README.md` — the runbook Tasks 4–5 follow.

- [ ] **Step 1: `.gitignore`**

```gitignore
# satellite/training/.gitignore — datasets, checkpoints, venv, household audio: never in git
.venv/
data/
output/
recordings/
```

- [ ] **Step 2: Author the two real configs from prod.yaml**

```bash
cp /tmp/lkww-ref/configs/prod.yaml satellite/training/ok_nabu_medium.yaml
```

Edit `ok_nabu_medium.yaml` — change ONLY these keys, keep every other prod value verbatim (RIR p, MUSAN SNR, `rounds: 3`, `steps: 100000`, `max_negative_weight: 3000`, `target_fp_per_hour: 0.1`, ACAV batch settings):

- `model_name: ok_nabu_medium`
- `target_phrases:` start with `["ok nabu", "okay nabboo", "oh keh nah boo"]` — English-Piper phonetic approximations of the Castilian "o-ké ná-bu"; final list fixed in Step 3's listening pass.
- `custom_negative_phrases:` REPLACE the "hey livekit" list with bilingual near-misses (starter list; extend freely, style mirrors upstream's own):
  `["ok", "nabu", "el nabo", "un nabo", "ok nada", "ok vamos", "ok bueno", "o cabo", "ok pavo", "ok guapo", "está bueno", "que apuro", "okay now boo", "okay nope", "hey nabu", "oh nabu", "ok tabu", "ok mambo"]`
- `model.model_size: medium` (already prod's value; `model.model_type` per the Task 1 gate decision)
- data/output dirs → `satellite/training/data` / `satellite/training/output` (same keys as smoke config)
- `tts_batch_size`: per the file's own comment ("50 for 8GB GPU, 100+ for 16GB+") sized to your GPU's VRAM.

Then: `sed -e 's/ok_nabu_medium/ok_nabu_small/' ok_nabu_medium.yaml > ok_nabu_small.yaml` and edit `model.model_size: small` in the copy. The two files must differ ONLY in `model_name` and `model.model_size` (verify: `diff ok_nabu_small.yaml ok_nabu_medium.yaml` shows exactly those lines).

- [ ] **Step 3: Spanish positives — VoxCPM2 config + phonetic-spelling listening pass**

1. Read `/tmp/lkww-ref/docs/data-generation.md` (and `configs/` examples) for the multilingual **VoxCPM2 voice-design** backend keys; add a Spanish (`es`) generation source for the literal phrase `"ok nabu"` to both real configs using the documented keys.
   - If one config cannot mix the Piper and VoxCPM2 backends: create `ok_nabu_es_gen.yaml` (same `model_name`, VoxCPM2-es backend) and document in the README that `generate` runs twice — once per config — before a single `augment` pass; verify with a tiny `n_samples` that the second run does not wipe the first's clips (if it does: generate into separate `data_dir`s and `cp` the positive WAVs together, the stages are file-based).
2. Listening pass (validates the phonetic spellings): temporarily set `n_samples: 15` in a scratch copy, run only `livekit-wakeword generate <scratch>.yaml`, then play a sample of positives (`paplay` under WSLg) for each spelling variant. Keep spellings that sound like "o-ké ná-bu", drop/replace ones that don't, fix the final `target_phrases` list in BOTH real configs.

- [ ] **Step 4: Write the README**

`satellite/training/README.md` must contain: pinned `livekit-wakeword` version (Task 2 Step 1), the apt dep list, venv setup commands, the three run commands (`setup`, `run ok_nabu_small.yaml`, `run ok_nabu_medium.yaml`), the recordings protocol (Task 4's commands verbatim, including the stop-service warning), the data layout (`data/`, `output/`, `recordings/{train,holdout}/`), the gate decision and what it forced, and where results get committed (`results/`).

- [ ] **Step 5: Commit**

```bash
git add satellite/training/.gitignore satellite/training/README.md satellite/training/*.yaml
git commit -m "feat(satellite): livekit-wakeword training configs for Spanish-first ok nabu"
```

---

### Task 4: real household recordings + holdout split (user participation required)

**Files:**
- Create: `satellite/training/recordings/train/<speaker>_<nn>.wav` (untracked)
- Create: `satellite/training/recordings/holdout/<speaker>_<nn>.wav` (untracked)

**Interfaces:**
- Consumes: README protocol from Task 3.
- Produces: `recordings/train/*.wav` (merged into training positives in Task 5), `recordings/holdout/*.wav` (Task 8 fixture source + Task 9 threshold calibration). All 16 kHz mono S16LE.

- [ ] **Step 1: Capture on the fran-office Pi (production mic path)**

```bash
ssh <user>@192.168.5.11
sudo systemctl stop nabu-satellite          # frees the exclusive plughw mic; leave nabu-micclock RUNNING
arecord -l                                   # note the XVF3800 card NAME (same card the service's mic command uses)
# Per take — speaker says "ok nabu" naturally, phrase ENDING near the clip end:
arecord -D plughw:CARD=<name>,DEV=0 -r 16000 -c 1 -f S16_LE -d 3 /tmp/rec/<speaker>_<nn>.wav
sudo systemctl start nabu-satellite          # IMMEDIATELY after the session
```

Target: 30–40 takes per household speaker, varied: normal/far (2–4 m), quiet/soft voice, a few with TV on. `scp` the takes to `satellite/training/recordings/`.

- [ ] **Step 2: Verify format + split**

```bash
cd satellite/training/recordings
for f in */*.wav; do soxi "$f" | grep -E 'Sample Rate|Channels|Precision'; done  # all 16000 / 1 / 16-bit
# Split ~75/25 per speaker: most takes -> train/, every 4th take -> holdout/
```

Holdout must contain at least 2 clips per speaker including one far-field take. Nothing is committed (gitignored); note the counts in the README's recordings section if they differ materially from the protocol.

---

### Task 5: full training runs (small + medium) + committed results

**Files:**
- Create: `satellite/training/results/ok_nabu_{small,medium}/` (metrics/eval JSON + DET png — committed)
- Modify: `satellite/training/README.md` (record wall-clock + recommended thresholds)

**Interfaces:**
- Consumes: configs (Task 3), `recordings/train/` (Task 4).
- Produces: `output/ok_nabu_small/ok_nabu_small.onnx` + `output/ok_nabu_medium/ok_nabu_medium.onnx` (untracked candidates for Tasks 6–8); `results/.../metrics.json` containing the **recommended operating threshold** each — Task 9 consumes the winner's.

- [ ] **Step 1: Inject real positives, then run generate**

Check `/tmp/lkww-ref/docs/training.md` + the config reference for a documented "additional/real positive clips" input; if none exists, run `livekit-wakeword generate ok_nabu_medium.yaml` first, then copy `recordings/train/*.wav` into the generated positive-clips directory under `data/` (find it: `find data -name '*.wav' -path '*positive*' | head`) so `augment` sweeps them together with the synthetic clips.

- [ ] **Step 2: Run both trainings (sequential, GPU-bound)**

```bash
cd satellite/training && source .venv/bin/activate
livekit-wakeword run ok_nabu_medium.yaml   # full pipeline; wall-clock unknown upstream — expect hours
livekit-wakeword run ok_nabu_small.yaml    # data generation reuses cached datasets where possible
```

Expected per run: `output/<name>/<name>.onnx`, `metrics.json`/eval JSON with recall/FPPH and the swept recommended threshold, DET curve png. Sanity bar before proceeding: eval recall ≥ 0.85 and FPPH ≤ 0.1 on its own validation — if wildly off, revisit Task 3 Step 3 (Spanish data weighting) before burning on-device time.

- [ ] **Step 3: Compat-check both candidates**

```bash
cp output/ok_nabu_small/ok_nabu_small.onnx output/ok_nabu_medium/ok_nabu_medium.onnx \
   ../tests/fixtures/clf/
(cd .. && cargo test --test tract_clf_compat -- --nocapture)
rm ../tests/fixtures/clf/ok_nabu_small.onnx ../tests/fixtures/clf/ok_nabu_medium.onnx
```

Expected: PASS for all files.

- [ ] **Step 4: Commit results (not models — the winner ships in Task 8)**

```bash
mkdir -p results/ok_nabu_small results/ok_nabu_medium
cp output/ok_nabu_small/*.json output/ok_nabu_small/*.png results/ok_nabu_small/
cp output/ok_nabu_medium/*.json output/ok_nabu_medium/*.png results/ok_nabu_medium/
git add results/ README.md
git commit -m "feat(satellite): ok nabu training runs — eval results and thresholds"
```

---

### Task 6: baseline acceptance measurement (stock model — BEFORE any deploy)

**Files:**
- Create: `satellite/training/results/acceptance.md`

**Interfaces:**
- Produces: the "before" row of the acceptance table; Task 9 fills the "after" row using the SAME protocol.

- [ ] **Step 1: Run the protocol against the currently deployed stock model**

On the fran-office unit, per condition, a wake attempt = one natural Spanish "ok nabu", success = satellite enters listening (chime/LED):
- 10 trials @ ~2 m, quiet room
- 10 trials @ ~4 m, quiet room
- 10 trials @ ~2–3 m, TV at conversational volume
- One TV evening (≥3 h): count spurious wakes (wake events with nobody addressing the device — cross-check `journalctl -u nabu-satellite | grep -c wake` against actual usage).

- [ ] **Step 2: Record + commit**

`satellite/training/results/acceptance.md` — table: condition | stock (n/10 or count) | new (blank) | target (≥9/10, ≥8/10, ≥8/10, <1/evening).

```bash
git add satellite/training/results/acceptance.md
git commit -m "docs(satellite): wake acceptance baseline for stock ok_nabu"
```

---

### Task 7: on-Pi candidate benchmark → pick the winner

**Files:**
- Temporarily modify: `satellite/models/ok_nabu.onnx` (restored after; the winner lands permanently in Task 8)
- Modify: `satellite/training/results/acceptance.md` (add a timing note)

**Interfaces:**
- Consumes: `output/ok_nabu_{small,medium}/*.onnx` (Task 5).
- Produces: the **winner artifact path** + its measured per-chunk µs, consumed by Task 8; timing recorded in `acceptance.md`.

- [ ] **Step 1: For each candidate (medium first), build + deploy + measure**

```bash
cp satellite/training/output/ok_nabu_medium/ok_nabu_medium.onnx satellite/models/ok_nabu.onnx
(cd satellite && scripts/build-release.sh)
scripts/provision-satellite-rs.sh <user>@192.168.5.11
ssh <user>@192.168.5.11 "sudo sed -i 's/RUST_LOG=info/RUST_LOG=nabu_satellite=debug/' \
  /etc/systemd/system/nabu-satellite.service && sudo systemctl daemon-reload && sudo systemctl restart nabu-satellite"
# Say "ok nabu" a few times, then:
ssh <user>@192.168.5.11 "journalctl -u nabu-satellite --since '-5 min' | grep 'wake inference' | tail -20"
```

Record the steady-state `us=` values. Repeat for `ok_nabu_small`. Winner = **medium if its per-chunk µs stays < 40 000 µs with headroom; else small** (Global Constraints).

- [ ] **Step 2: Restore repo state + revert Pi logging**

```bash
git checkout -- satellite/models/ok_nabu.onnx
ssh <user>@192.168.5.11 "sudo sed -i 's/RUST_LOG=nabu_satellite=debug/RUST_LOG=info/' \
  /etc/systemd/system/nabu-satellite.service && sudo systemctl daemon-reload && sudo systemctl restart nabu-satellite"
```

Note: the Pi keeps running the last-provisioned candidate binary until Task 9's final deploy — acceptable (it's a trained model either way), but do Task 8 + 9 promptly.

- [ ] **Step 3: Record timings in `acceptance.md`, commit**

```bash
git add satellite/training/results/acceptance.md
git commit -m "docs(satellite): on-A53 timing for ok nabu candidates, winner chosen"
```

---

### Task 8: fixture replacement + model swap (TDD) + license update

**Files:**
- Modify: `satellite/tests/fixtures/ok_nabu.wav` (replaced with a Spanish-pronounced real recording)
- Modify: `satellite/models/ok_nabu.onnx` (replaced with the winner)
- Modify: `satellite/models/LICENSE-models.md`

**Interfaces:**
- Consumes: winner artifact (Task 7), a `recordings/holdout/` clip (Task 4).
- Produces: the shipping model + fixture; all existing tests (`detector.rs` unit tests, `spike_wake.rs`) now pin the NEW model's behavior.

- [ ] **Step 1: Build the new fixture from a real holdout recording**

```bash
# Pick a clear, normal-distance holdout take; end-align the phrase like training data:
sox satellite/training/recordings/holdout/<speaker>_<nn>.wav -r 16000 -c 1 -b 16 \
  satellite/tests/fixtures/ok_nabu.wav
soxi satellite/tests/fixtures/ok_nabu.wav   # 16000 Hz, 1 ch, 16-bit, ~2.5-3 s
```

- [ ] **Step 2: Run tests — expect RED with the stock model**

Run: `cd satellite && cargo test`
Expected: `fires_once_on_ok_nabu_then_respects_refractory` and/or `fires_on_ok_nabu` FAIL (stock English model missing the Spanish-pronounced clip — this is the bug, demonstrated). If they PASS, record that the stock model does fire on this particular clip and continue — the field data, not this fixture, justifies the swap.

- [ ] **Step 3: Swap in the winner**

```bash
cp satellite/training/output/<winner>/<winner>.onnx satellite/models/ok_nabu.onnx
```

- [ ] **Step 4: Run tests — expect GREEN**

Run: `cd satellite && cargo test`
Expected: ALL pass — detector unit tests (fires-once, silence, shared-bundle, refractory), `spike_wake.rs` score tests, `tract_clf_compat`. If `silent_on_silence`/`quiet_on_silence` fails, the model is unusable regardless of eval numbers — go back to Task 5 Step 2's sanity bar.

- [ ] **Step 5: Update `satellite/models/LICENSE-models.md`**

Replace the `ok_nabu.onnx` table row and trailing note: source = `satellite/training/` (self-trained with livekit-wakeword `<pinned version>`, config `ok_nabu_<winner>.yaml`, results in `training/results/`), license = own artifact (trained from scratch; livekit-wakeword is Apache-2.0). The melspectrogram/embedding rows and their CC-BY-NC-SA note stay unchanged; drop "The three ONNX models... are CC-BY-NC-SA" phrasing to "The two frontend models...".

- [ ] **Step 6: Commit**

```bash
git add satellite/tests/fixtures/ok_nabu.wav satellite/models/ok_nabu.onnx satellite/models/LICENSE-models.md
git commit -m "feat(satellite): ship self-trained Spanish-first ok nabu classifier"
```

---

### Task 9: deploy defaults, final deploy, calibration + acceptance (after)

**Files:**
- Modify: `satellite/deploy/nabu-satellite.service` (lines with `--threshold 0.7` / `--trigger-level 2`)
- Modify: `scripts/provision-satellite-rs.sh` (same two flags, ~lines 340-341)
- Modify: `satellite/training/results/acceptance.md` ("after" column)

**Interfaces:**
- Consumes: winner's recommended threshold from `results/<winner>/metrics.json` (Task 5), holdout clips (Task 4), baseline table (Task 6).
- Produces: the tuned production operating point + the completed acceptance table.

- [ ] **Step 1: Set the new operating point in both deploy files**

In `satellite/deploy/nabu-satellite.service` and `scripts/provision-satellite-rs.sh`: `--threshold <metrics.json recommended, rounded to 2 dp>` and `--trigger-level 1` (0.7/2 was compensation for the stock model; the new threshold comes from a sweep targeting FPPH ≤ 0.1).

- [ ] **Step 2: Build, deploy, enable debug scoring**

```bash
(cd satellite && cargo test && scripts/build-release.sh)
scripts/provision-satellite-rs.sh <user>@192.168.5.11
ssh <user>@192.168.5.11 "sudo sed -i 's/RUST_LOG=info/RUST_LOG=nabu_satellite=debug/' \
  /etc/systemd/system/nabu-satellite.service && sudo systemctl daemon-reload && sudo systemctl restart nabu-satellite"
```

(No qemu smoke: there is no qemu script in `satellite/scripts/`, and per `satellite/CLAUDE.md` qemu needs `--no-wake` — which disables exactly the path this change touches. `cargo test` + on-device validation are the gates.)

- [ ] **Step 3: Calibrate over 2–3 days of real use**

Watch `journalctl -u nabu-satellite | grep 'wake score'`: real "ok nabu" utterances should score comfortably above threshold (misses cluster just below → lower threshold by 0.05 steps); ambient TV speech should stay well below (spurious wakes → raise by 0.05, or restore `--trigger-level 2` as the LAST resort). Each adjustment: edit the two deploy files, re-provision (keeps repo = deployed truth).

- [ ] **Step 4: Run the acceptance protocol (identical to Task 6) and fill the "after" column**

Acceptance (spec): ≥9/10 @ 2 m quiet, ≥8/10 @ 4 m quiet, ≥8/10 with TV, <1 spurious/TV evening. If a bar is missed, iterate Step 3 first; if it cannot be met at any operating point, that's a training-data problem — reopen Task 3 Step 3 (data weighting) rather than tuning forever.

- [ ] **Step 5: Revert debug logging, commit the tuned values**

```bash
ssh <user>@192.168.5.11 "sudo sed -i 's/RUST_LOG=nabu_satellite=debug/RUST_LOG=info/' \
  /etc/systemd/system/nabu-satellite.service && sudo systemctl daemon-reload && sudo systemctl restart nabu-satellite"
git add satellite/deploy/nabu-satellite.service scripts/provision-satellite-rs.sh \
  satellite/training/results/acceptance.md
git commit -m "feat(satellite): recalibrated wake operating point for retrained model"
```

---

### Task 10: cleanup + spec closeout

**Files:**
- Delete: `satellite/tests/fixtures/clf/hey_livekit.onnx` + `satellite/tests/fixtures/clf/`
- Delete: `satellite/tests/tract_clf_compat.rs`
- Modify: `docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md` (Status)
- Modify: `satellite/CLAUDE.md` (one line: model provenance now `satellite/training/`)

**Interfaces:**
- Consumes: everything shipped. The compat coverage the deleted test provided now lives in `spike_wake.rs` + `detector.rs` tests, which exercise the SHIPPED model's ops via `include_bytes!`.

- [ ] **Step 1: Remove the temporary gate test + third-party fixture**

```bash
git rm -r satellite/tests/fixtures/clf satellite/tests/tract_clf_compat.rs
cd satellite && cargo test   # everything still green without them
```

- [ ] **Step 2: Spec + CLAUDE.md closeout**

Spec: `**Status:** Implemented (YYYY-MM-DD)` + one-line result summary (winner size, final threshold, acceptance table result). `satellite/CLAUDE.md`: in the intro paragraph, note the classifier is self-trained via `satellite/training/` (livekit-wakeword), replacing the community model.

- [ ] **Step 3: Commit**

```bash
git add -u && git add docs/superpowers/specs/2026-07-17-wake-word-reliability-design.md satellite/CLAUDE.md
git commit -m "chore(satellite): close out wake reliability spec, drop temporary compat gate"
```
