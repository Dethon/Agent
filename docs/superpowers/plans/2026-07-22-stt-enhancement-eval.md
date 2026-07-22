# STT Enhancement Eval Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an offline eval harness that measures whether GTCRN / DeepFilterNet3 / WeSep-TSE preprocessing cuts whisper WER on Spanish commands mixed with TV-dialogue/music interference, and run the round-1 sweep.

**Architecture:** A staged, manifest-driven Python CLI in `scripts/stt-enhancement-eval/` (spec: `docs/superpowers/specs/2026-07-22-stt-enhancement-eval-design.md`). Stages: `fetch` → `validate` → `mix` → `process` → `transcribe` → `report`. Every stage reads/writes JSONL manifests plus WAVs under a gitignored `runs/<run>/` tree and skips work whose outputs already exist.

**Tech Stack:** Python 3.11 via uv; numpy, soundfile, soxr, jiwer, onnxruntime (GTCRN), deepfilternet + CPU torch (DFN3), faster-whisper (local backend), huggingface_hub (TSE checkpoint), stdlib-only Wyoming worker run inside the compose network via `docker run`.

## Global Constraints

- All work happens on the currently checked-out branch (`noise`). Never switch branches.
- Base dir for everything: `scripts/stt-enhancement-eval/`. All commands below run from there unless a path is shown.
- Python pinned via uv (`requires-python = ">=3.11"`); torch/torchaudio come from the PyTorch **CPU** wheel index.
- `data/` (downloads) and `runs/` (artifacts) are gitignored; never commit WAVs or model weights.
- No new environment variables — everything is a CLI flag with a default (per repo rule, env vars would require compose/appsettings skeletons).
- Audio interchange format everywhere: 16 kHz mono, WAV. Float32 in memory (−1..1), S16LE on disk.
- Reference texts and take→phrase mapping must match `scripts/enroll-voice.sh` **verbatim** (5 phrases, 5 conditions, `enroll-<i>.wav`, i starts at 1).
- TDD for pure logic (mapping, normalization, mixing, manifests, aggregation): failing test first, watch it fail, then implement. Model/network stages get one-file smoke runs instead.

---

### Task 1: Scaffold the eval package

**Files:**
- Create: `scripts/stt-enhancement-eval/pyproject.toml`
- Create: `scripts/stt-enhancement-eval/.gitignore`
- Create: `scripts/stt-enhancement-eval/stt_eval/__init__.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Create: `scripts/stt-enhancement-eval/tests/test_cli.py`

**Interfaces:**
- Produces: `stt_eval` package; CLI entry `uv run python -m stt_eval <stage>` with an argparse subcommand registry `STAGES: dict[str, Callable[[argparse.Namespace], None]]` in `__main__.py`. Later tasks add stages by inserting into `STAGES` and extending `build_parser()`.

- [ ] **Step 1: Write the failing test**

`tests/test_cli.py`:
```python
import subprocess
import sys


def test_cli_lists_stages():
    out = subprocess.run(
        [sys.executable, "-m", "stt_eval", "--help"],
        capture_output=True, text=True,
    )
    assert out.returncode == 0
    assert "fetch" in out.stdout
```

- [ ] **Step 2: Create pyproject and gitignore**

`pyproject.toml`:
```toml
[project]
name = "stt-eval"
version = "0.1.0"
description = "Offline STT enhancement eval harness (see docs/superpowers/specs/2026-07-22-stt-enhancement-eval-design.md)"
requires-python = ">=3.11,<3.13"
dependencies = [
    "numpy>=1.26",
    "soundfile>=0.12",
    "soxr>=0.4",
    "jiwer>=3.0",
    "tqdm>=4.66",
    "onnxruntime>=1.17",
    "faster-whisper>=1.0",
    "deepfilternet==0.5.6",
    "torch>=2.2",
    "torchaudio>=2.2",
    "huggingface_hub>=0.23",
]

[dependency-groups]
dev = ["pytest>=8.0"]

[[tool.uv.index]]
name = "pytorch-cpu"
url = "https://download.pytorch.org/whl/cpu"
explicit = true

[tool.uv.sources]
torch = { index = "pytorch-cpu" }
torchaudio = { index = "pytorch-cpu" }

[tool.pytest.ini_options]
testpaths = ["tests"]

[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[tool.hatch.build.targets.wheel]
packages = ["stt_eval"]
```

`.gitignore`:
```
data/
runs/
__pycache__/
*.egg-info/
.venv/
```

- [ ] **Step 3: Write the CLI skeleton**

`stt_eval/__init__.py`: empty file.

`stt_eval/__main__.py`:
```python
"""Staged STT-enhancement eval CLI. Stages: fetch, validate, mix, process, transcribe, report."""
import argparse
from collections.abc import Callable

STAGES: dict[str, Callable[[argparse.Namespace], None]] = {}


def _todo(args: argparse.Namespace) -> None:
    raise SystemExit(f"stage not implemented yet")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="stt_eval", description=__doc__)
    sub = parser.add_subparsers(dest="stage", required=True, metavar="{fetch,validate,mix,process,transcribe,report}")
    for name in ("fetch", "validate", "mix", "process", "transcribe", "report"):
        p = sub.add_parser(name)
        p.set_defaults(func=STAGES.get(name, _todo))
        _add_stage_args(name, p)
    return parser


def _add_stage_args(name: str, p: argparse.ArgumentParser) -> None:
    p.add_argument("--run", default="round1", help="run name under runs/")
    p.add_argument("--data", default="data", help="downloads cache dir")


def main() -> None:
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Install env, run test, verify it passes**

Run: `cd scripts/stt-enhancement-eval && uv sync && uv run pytest tests/test_cli.py -v`
Expected: PASS (help text lists all six stages). Note: first `uv sync` downloads torch-cpu etc. (~1–2 GB, minutes).

- [ ] **Step 5: Commit**

```bash
git add scripts/stt-enhancement-eval/pyproject.toml scripts/stt-enhancement-eval/.gitignore scripts/stt-enhancement-eval/stt_eval/ scripts/stt-enhancement-eval/tests/
git commit -m "feat(stt-eval): scaffold staged eval CLI package"
```
(Repo hook note: pre-commit formats .cs only; irrelevant here. `uv.lock` will appear after sync — commit it too.)

---

### Task 2: Take→phrase mapping

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/phrases.py`
- Test: `scripts/stt-enhancement-eval/tests/test_phrases.py`

**Interfaces:**
- Produces: `PHRASES: list[str]` (5 items, verbatim from enroll-voice.sh) and `phrase_for_take(take_index: int) -> str` (1-based index, mirrors the bash: `cond=(i-1)%5; idx=(cond+(i-1)//5)%5`).

- [ ] **Step 1: Write the failing test**

`tests/test_phrases.py`:
```python
from stt_eval.phrases import PHRASES, phrase_for_take


def test_five_phrases():
    assert len(PHRASES) == 5
    assert PHRASES[3] == "Pon un temporizador de diez minutos para la pasta que está al fuego."


def test_mapping_matches_bash_formula():
    # bash: cond=(i-1)%5 ; idx=(cond + (i-1)/5) % 5   (integer division)
    assert phrase_for_take(1) == PHRASES[0]
    assert phrase_for_take(5) == PHRASES[4]
    assert phrase_for_take(6) == PHRASES[1]   # second pass shifts by one
    assert phrase_for_take(10) == PHRASES[0]
    assert phrase_for_take(11) == PHRASES[2]  # third pass shifts by two
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_phrases.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'stt_eval.phrases'`

- [ ] **Step 3: Implement**

`stt_eval/phrases.py`:
```python
"""Reference phrases and take->phrase mapping, mirroring scripts/enroll-voice.sh verbatim."""

PHRASES = [
    "Ok nabu, pon música tranquila en el salón y baja un poco el volumen, por favor.",
    "¿Qué tiempo va a hacer mañana por la tarde aquí en casa?",
    "Recuérdame sacar la basura esta noche antes de irme a dormir.",
    "Pon un temporizador de diez minutos para la pasta que está al fuego.",
    "Dime las noticias de hoy y cómo está el tráfico para ir al centro.",
]

_N_CONDITIONS = 5


def phrase_for_take(take_index: int) -> str:
    cond = (take_index - 1) % _N_CONDITIONS
    idx = (cond + (take_index - 1) // _N_CONDITIONS) % len(PHRASES)
    return PHRASES[idx]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_phrases.py -v`
Expected: PASS (5 assertions)

- [ ] **Step 5: Commit**

```bash
git add stt_eval/phrases.py tests/test_phrases.py
git commit -m "feat(stt-eval): take->phrase mapping mirroring enroll-voice.sh"
```

---

### Task 3: Text normalization

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/textnorm.py`
- Test: `scripts/stt-enhancement-eval/tests/test_textnorm.py`

**Interfaces:**
- Produces: `normalize(text: str) -> str` — casefold, strip all punctuation (incl. ¿¡«»…), collapse whitespace, **keep** diacritics/ñ.

- [ ] **Step 1: Write the failing test**

`tests/test_textnorm.py`:
```python
from stt_eval.textnorm import normalize


def test_strips_punctuation_keeps_diacritics():
    assert normalize("¿Qué tiempo va a hacer mañana?") == "qué tiempo va a hacer mañana"


def test_casefold_and_whitespace():
    assert normalize("  Ok   NABU,  pon música. ") == "ok nabu pon música"


def test_ellipsis_and_dashes():
    assert normalize("Dime — las noticias… ya") == "dime las noticias ya"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_textnorm.py -v`
Expected: FAIL with `ModuleNotFoundError`

- [ ] **Step 3: Implement**

`stt_eval/textnorm.py`:
```python
import re

_NON_WORD = re.compile(r"[^\w\s]", re.UNICODE)


def normalize(text: str) -> str:
    return " ".join(_NON_WORD.sub(" ", text.casefold()).split())
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_textnorm.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add stt_eval/textnorm.py tests/test_textnorm.py
git commit -m "feat(stt-eval): whisper-style Spanish text normalization"
```

---

### Task 4: SNR mixer

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/mixing.py`
- Test: `scripts/stt-enhancement-eval/tests/test_mixing.py`

**Interfaces:**
- Produces:
  - `active_rms(x: np.ndarray, frame: int = 512) -> float` — RMS over frames whose RMS ≥ 0.1 × max frame RMS (the "speech-active region" from the spec).
  - `mix_at_snr(speech: np.ndarray, interference: np.ndarray, snr_db: float) -> np.ndarray` — scales interference so `active_rms(speech) / rms(interference_scaled)` hits the target SNR, adds, peak-normalizes to ≤0.99 if needed. Inputs/outputs float32 −1..1; interference is cropped/tiled to `len(speech)` by the caller (Task 5).

- [ ] **Step 1: Write the failing test**

`tests/test_mixing.py`:
```python
import numpy as np
from stt_eval.mixing import active_rms, mix_at_snr

SR = 16000


def _tone(seconds, freq=440.0, amp=0.3):
    t = np.arange(int(SR * seconds)) / SR
    return (amp * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def test_active_rms_ignores_silence():
    burst = np.concatenate([np.zeros(SR, np.float32), _tone(1.0)])
    # Active RMS of half-silence signal ~= RMS of the tone alone, not diluted by silence.
    assert abs(active_rms(burst) - active_rms(_tone(1.0))) < 0.01


def test_mix_hits_target_snr():
    rng = np.random.default_rng(42)
    speech = _tone(2.0)
    noise = rng.normal(0, 0.05, SR * 2).astype(np.float32)
    mixed = mix_at_snr(speech, noise, snr_db=5.0)
    residual = mixed - speech  # scaled interference (no clipping at these levels)
    measured = 20 * np.log10(active_rms(speech) / np.sqrt(np.mean(residual**2)))
    assert abs(measured - 5.0) < 0.5


def test_mix_never_clips():
    speech = _tone(1.0, amp=0.9)
    noise = _tone(1.0, freq=200.0, amp=0.9)
    mixed = mix_at_snr(speech, noise, snr_db=0.0)
    assert np.max(np.abs(mixed)) <= 0.99 + 1e-6
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_mixing.py -v`
Expected: FAIL with `ModuleNotFoundError`

- [ ] **Step 3: Implement**

`stt_eval/mixing.py`:
```python
import numpy as np


def active_rms(x: np.ndarray, frame: int = 512) -> float:
    n = len(x) // frame * frame
    frames = x[:n].reshape(-1, frame)
    rms = np.sqrt(np.mean(frames.astype(np.float64) ** 2, axis=1))
    thr = 0.1 * rms.max()
    act = rms[rms >= thr]
    return float(np.sqrt(np.mean(act**2)))


def mix_at_snr(speech: np.ndarray, interference: np.ndarray, snr_db: float) -> np.ndarray:
    s = active_rms(speech)
    i = float(np.sqrt(np.mean(interference.astype(np.float64) ** 2)))
    scale = s / (i * 10 ** (snr_db / 20))
    mixed = speech + interference * scale
    peak = float(np.max(np.abs(mixed)))
    if peak > 0.99:
        mixed = mixed * (0.99 / peak)
    return mixed.astype(np.float32)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_mixing.py -v`
Expected: PASS (3 tests). Note `test_mix_hits_target_snr` measures against **active** speech RMS; tolerance 0.5 dB.

- [ ] **Step 5: Commit**

```bash
git add stt_eval/mixing.py tests/test_mixing.py
git commit -m "feat(stt-eval): active-RMS SNR mixer with clipping guard"
```

---

### Task 5: Manifest module + `mix` stage

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/manifest.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/mix_stage.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py` (register stage + args)
- Test: `scripts/stt-enhancement-eval/tests/test_manifest.py`, `scripts/stt-enhancement-eval/tests/test_mix_stage.py`

**Interfaces:**
- Consumes: `phrase_for_take`, `mix_at_snr`.
- Produces:
  - `manifest.py`: `@dataclass Utterance(id: str, speaker: str, take: int, wav: str, reference: str, interference: str, snr_db: float | None)`; `read_manifest(path: Path) -> list[Utterance]`; `write_manifest(path: Path, rows: list[Utterance]) -> None` (JSONL, one object per line, `wav` relative to the manifest's directory).
  - `mix_stage.py`: `run_mix(voices_dir: Path, data_dir: Path, run_dir: Path, seed: int, takes_file: Path | None) -> None`. Writes `run_dir/corpus/<id>.wav` + `run_dir/manifest.jsonl`. IDs: `<speaker>-t<take>-<interference>-snr<+dd|clean>`. Grid per take: clean row (`interference="none"`, `snr_db=None`) + {speech, music} × {15, 10, 5, 0, −5} dB. If `takes_file` (from Task 8's `validate`) exists, exclude takes marked `included: false`; else use all takes.
  - Interference beds (also in `mix_stage.py`): `build_speech_bed(files: list[Path], rng, n_samples: int) -> np.ndarray` (concat random clips, resample to 16 k mono via soxr, crop) and `build_music_bed(...)` (random file, random offset, tile if short). Both return float32.

- [ ] **Step 1: Write the failing manifest test**

`tests/test_manifest.py`:
```python
from pathlib import Path
from stt_eval.manifest import Utterance, read_manifest, write_manifest


def test_roundtrip(tmp_path: Path):
    rows = [
        Utterance(id="fran-t1-none-clean", speaker="fran", take=1, wav="corpus/a.wav",
                  reference="hola", interference="none", snr_db=None),
        Utterance(id="fran-t1-speech-snr-05", speaker="fran", take=1, wav="corpus/b.wav",
                  reference="hola", interference="speech", snr_db=-5.0),
    ]
    p = tmp_path / "manifest.jsonl"
    write_manifest(p, rows)
    assert read_manifest(p) == rows
    assert len(p.read_text().strip().splitlines()) == 2
```

- [ ] **Step 2: Run to verify it fails, implement manifest.py**

Run: `uv run pytest tests/test_manifest.py -v` → FAIL (`ModuleNotFoundError`).

`stt_eval/manifest.py`:
```python
import json
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass
class Utterance:
    id: str
    speaker: str
    take: int
    wav: str
    reference: str
    interference: str
    snr_db: float | None


def write_manifest(path: Path, rows: list[Utterance]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(asdict(r), ensure_ascii=False) + "\n")


def read_manifest(path: Path) -> list[Utterance]:
    with path.open(encoding="utf-8") as f:
        return [Utterance(**json.loads(line)) for line in f if line.strip()]
```

Run: `uv run pytest tests/test_manifest.py -v` → PASS.

- [ ] **Step 3: Write the failing mix-stage test**

`tests/test_mix_stage.py` (synthetic voices/interference fixtures, no real data):
```python
import numpy as np
import soundfile as sf
from pathlib import Path
from stt_eval.manifest import read_manifest
from stt_eval.mix_stage import run_mix

SR = 16000


def _write_wav(path: Path, seconds: float, freq: float):
    path.parent.mkdir(parents=True, exist_ok=True)
    t = np.arange(int(SR * seconds)) / SR
    sf.write(path, (0.3 * np.sin(2 * np.pi * freq * t)).astype(np.float32), SR, subtype="PCM_16")


def _setup(tmp_path: Path):
    _write_wav(tmp_path / "voices/fran/enroll-1.wav", 2.0, 440)
    _write_wav(tmp_path / "voices/fran/enroll-2.wav", 2.0, 330)
    _write_wav(tmp_path / "data/interference/speech/clip1.wav", 3.0, 220)
    _write_wav(tmp_path / "data/interference/music/song1.wav", 3.0, 110)
    return tmp_path / "voices", tmp_path / "data", tmp_path / "runs/test"


def test_grid_and_determinism(tmp_path: Path):
    voices, data, run = _setup(tmp_path)
    run_mix(voices, data, run, seed=7, takes_file=None)
    rows = read_manifest(run / "manifest.jsonl")
    assert len(rows) == 2 * (1 + 2 * 5)  # 2 takes x (clean + 2 interference types x 5 SNRs)
    clean = [r for r in rows if r.interference == "none"]
    assert all(r.snr_db is None for r in clean)
    first = sf.read(run / rows[1].wav)[0]
    run_mix(voices, data, tmp_path / "runs/test2", seed=7, takes_file=None)
    rows2 = read_manifest(tmp_path / "runs/test2/manifest.jsonl")
    again = sf.read(tmp_path / "runs/test2" / rows2[1].wav)[0]
    assert np.array_equal(first, again)  # same seed -> identical corpus
```

- [ ] **Step 4: Run to verify it fails, implement mix_stage.py**

Run: `uv run pytest tests/test_mix_stage.py -v` → FAIL.

`stt_eval/mix_stage.py`:
```python
"""Builds the synthetic corpus: enrollment takes x {clean, speech-bed, music-bed} x SNR grid."""
import json
from pathlib import Path

import numpy as np
import soundfile as sf
import soxr

from .manifest import Utterance, write_manifest
from .mixing import mix_at_snr
from .phrases import phrase_for_take

SR = 16000
SNRS = [15.0, 10.0, 5.0, 0.0, -5.0]


def _load_16k_mono(path: Path) -> np.ndarray:
    audio, sr = sf.read(path, dtype="float32", always_2d=True)
    mono = audio.mean(axis=1)
    return soxr.resample(mono, sr, SR).astype(np.float32) if sr != SR else mono


def build_speech_bed(files: list[Path], rng: np.random.Generator, n_samples: int) -> np.ndarray:
    parts, total = [], 0
    while total < n_samples:
        clip = _load_16k_mono(files[rng.integers(len(files))])
        parts.append(clip)
        total += len(clip)
    return np.concatenate(parts)[:n_samples]


def build_music_bed(files: list[Path], rng: np.random.Generator, n_samples: int) -> np.ndarray:
    audio = _load_16k_mono(files[rng.integers(len(files))])
    if len(audio) < n_samples:
        audio = np.tile(audio, n_samples // len(audio) + 1)
    start = int(rng.integers(0, len(audio) - n_samples + 1)) if len(audio) > n_samples else 0
    return audio[start:start + n_samples]


def _excluded_takes(takes_file: Path | None) -> set[tuple[str, int]]:
    if takes_file is None or not takes_file.exists():
        return set()
    out = set()
    with takes_file.open(encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            if not row["included"]:
                out.add((row["speaker"], row["take"]))
    return out


def run_mix(voices_dir: Path, data_dir: Path, run_dir: Path, seed: int, takes_file: Path | None) -> None:
    rng = np.random.default_rng(seed)
    speech_files = sorted((data_dir / "interference/speech").rglob("*.wav"))
    music_files = sorted((data_dir / "interference/music").rglob("*.wav"))
    if not speech_files or not music_files:
        raise SystemExit("interference beds missing - run the fetch stage first")
    excluded = _excluded_takes(takes_file)
    corpus = run_dir / "corpus"
    corpus.mkdir(parents=True, exist_ok=True)
    rows: list[Utterance] = []
    for take_wav in sorted(voices_dir.glob("*/enroll-*.wav")):
        speaker = take_wav.parent.name
        take = int(take_wav.stem.split("-")[1])
        if (speaker, take) in excluded:
            continue
        reference = phrase_for_take(take)
        speech = _load_16k_mono(take_wav)
        variants: list[tuple[str, float | None, np.ndarray]] = [("none", None, speech)]
        for kind, files, builder in (("speech", speech_files, build_speech_bed),
                                     ("music", music_files, build_music_bed)):
            bed = builder(files, rng, len(speech))
            for snr in SNRS:
                variants.append((kind, snr, mix_at_snr(speech, bed, snr)))
        for kind, snr, audio in variants:
            suffix = "clean" if snr is None else f"snr{int(snr):+03d}"
            uid = f"{speaker}-t{take}-{kind}-{suffix}"
            sf.write(corpus / f"{uid}.wav", audio, SR, subtype="PCM_16")
            rows.append(Utterance(uid, speaker, take, f"corpus/{uid}.wav", reference, kind, snr))
    write_manifest(run_dir / "manifest.jsonl", rows)
    print(f"wrote {len(rows)} utterances to {run_dir}")
```

Register in `__main__.py` — replace `_add_stage_args` and the import block:
```python
from pathlib import Path


def _add_stage_args(name: str, p: argparse.ArgumentParser) -> None:
    p.add_argument("--run", default="round1", help="run name under runs/")
    p.add_argument("--data", default="data", help="downloads cache dir")
    p.add_argument("--voices", default="data/voices", help="enrollment takes dir")
    if name == "mix":
        p.add_argument("--seed", type=int, default=7)


def _mix(args: argparse.Namespace) -> None:
    from .mix_stage import run_mix
    run_dir = Path("runs") / args.run
    takes = run_dir / "takes.jsonl"
    run_mix(Path(args.voices), Path(args.data), run_dir, args.seed,
            takes if takes.exists() else None)


STAGES["mix"] = _mix
```
(Place `STAGES["mix"] = _mix` after the function definitions, before `build_parser` is called at runtime — module level is fine.)

- [ ] **Step 5: Run all tests, commit**

Run: `uv run pytest -v`
Expected: PASS (cli, phrases, textnorm, mixing, manifest, mix_stage).

```bash
git add stt_eval/manifest.py stt_eval/mix_stage.py stt_eval/__main__.py tests/test_manifest.py tests/test_mix_stage.py
git commit -m "feat(stt-eval): manifest module and synthetic-corpus mix stage"
```

---

### Task 6: `fetch` stage (voices, OpenSLR Spanish, MUSAN music, GTCRN model)

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/fetch_stage.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`

**Interfaces:**
- Consumes: nothing (network + ssh).
- Produces on disk (all idempotent — skip if target exists and is non-empty):
  - `data/voices/<speaker>/enroll-<i>.wav` — scp'd from the pi.
  - `data/interference/speech/**/*.wav` — OpenSLR SLR73 (crowdsourced Peruvian Spanish; dialect is irrelevant for background beds).
  - `data/interference/music/**/*.wav` — MUSAN music subset, stream-extracted (never stores the full 10 GB tarball).
  - `data/models/gtcrn_simple.onnx` — official streaming checkpoint.

- [ ] **Step 1: Implement fetch_stage.py**

```python
"""Idempotent downloads: enrollment voices (scp), SLR73 Spanish speech, MUSAN music, GTCRN onnx."""
import subprocess
from pathlib import Path

PI_DEFAULT = "dethon@192.168.5.45:/home/dethon/jackbot/docker-compose/volumes/voices"
SLR73_URLS = [
    "https://www.openslr.org/resources/73/es_pe_female.zip",
    "https://www.openslr.org/resources/73/es_pe_male.zip",
]
MUSAN_URL = "https://www.openslr.org/resources/17/musan.tar.gz"
GTCRN_URL = "https://github.com/Xiaobin-Rong/gtcrn/raw/main/stream/onnx_models/gtcrn_simple.onnx"


def _have(path: Path) -> bool:
    return path.exists() and any(path.iterdir()) if path.is_dir() else path.exists()


def _sh(cmd: str) -> None:
    print(f"+ {cmd}")
    subprocess.run(cmd, shell=True, check=True)


def run_fetch(data_dir: Path, pi_source: str) -> None:
    voices = data_dir / "voices"
    if not _have(voices):
        voices.mkdir(parents=True, exist_ok=True)
        _sh(f"scp -r '{pi_source}/'* '{voices}/'")
    for spk in sorted(voices.iterdir()):
        if spk.is_dir():
            print(f"voices/{spk.name}: {len(list(spk.glob('enroll-*.wav')))} takes")

    speech = data_dir / "interference/speech"
    if not _have(speech):
        speech.mkdir(parents=True, exist_ok=True)
        for url in SLR73_URLS:
            zip_path = speech / url.rsplit("/", 1)[1]
            _sh(f"curl -fL --retry 3 -o '{zip_path}' '{url}'")
            _sh(f"unzip -q -o '{zip_path}' -d '{speech}' && rm '{zip_path}'")
        # If a URL 404s, check https://www.openslr.org/73/ for current filenames.

    music = data_dir / "interference/music"
    if not _have(music):
        music.mkdir(parents=True, exist_ok=True)
        # Stream-extract only musan/music from the 10 GB tarball; nothing else touches disk.
        _sh(f"curl -fL '{MUSAN_URL}' | tar -xz -C '{music}' --strip-components=1 musan/music")

    model = data_dir / "models/gtcrn_simple.onnx"
    if not model.exists():
        model.parent.mkdir(parents=True, exist_ok=True)
        _sh(f"curl -fL --retry 3 -o '{model}' '{GTCRN_URL}'")
    print("fetch complete")
```

Register in `__main__.py` (inside `_add_stage_args`, add under `if name == "fetch":` → `p.add_argument("--pi", default=fetch_stage.PI_DEFAULT)`; stage fn):
```python
def _fetch(args: argparse.Namespace) -> None:
    from .fetch_stage import run_fetch
    run_fetch(Path(args.data), args.pi)


STAGES["fetch"] = _fetch
```
(Import `PI_DEFAULT` lazily: in `_add_stage_args`, use the literal default string rather than importing the module at parse time — copy the constant: `p.add_argument("--pi", default="dethon@192.168.5.45:/home/dethon/jackbot/docker-compose/volumes/voices")`.)

- [ ] **Step 2: Smoke-run fetch**

Run: `uv run python -m stt_eval fetch`
Expected: voices scp'd with per-speaker take counts printed; SLR73 zips downloaded+extracted (~1 GB); MUSAN music stream-extracted (~10 GB transferred, only music kept, takes a while); `data/models/gtcrn_simple.onnx` present (a few hundred kB). Re-run → all steps skipped, instant.

Sanity check: `ls data/interference/speech | head; ls data/interference/music | head; python -c "import soundfile; print(soundfile.info(next(__import__('pathlib').Path('data/interference/music').rglob('*.wav')).as_posix()))"` → MUSAN wavs are 16 kHz mono.

- [ ] **Step 3: Commit**

```bash
git add stt_eval/fetch_stage.py stt_eval/__main__.py
git commit -m "feat(stt-eval): idempotent fetch stage (voices, SLR73, MUSAN music, GTCRN)"
```

---

### Task 7: Transcription backends + `transcribe` stage

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/backends.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/wyoming_worker.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Test: `scripts/stt-enhancement-eval/tests/test_wyoming_framing.py`

**Interfaces:**
- Consumes: `read_manifest`.
- Produces:
  - `backends.py`: `transcribe_files(backend: str, wavs: list[Path], out_jsonl: Path) -> None`. Output JSONL rows: `{"wav": "<abs path str>", "text": str, "score": float | None}`. Skips wavs already present in `out_jsonl` (append mode = resumable).
    - backend `"medium"`: faster-whisper `WhisperModel("medium", device="auto", compute_type="int8")`, `language="es"`, `beam_size=5`; `text` = concatenated segment texts; `score` = mean of segment `avg_logprob` mapped via `math.exp`.
    - backend `"wyoming"`: shells out to `docker run --rm --network <jackbot net> -v <workdir>:/work -v <pkg>/stt_eval:/s:ro python:3.12-slim python /s/wyoming_worker.py --manifest /work/in.jsonl --out /work/out.jsonl` after writing wav paths (rewritten to `/work/...`) into `in.jsonl`. Requires the compose stack up.
  - `wyoming_worker.py`: **stdlib-only** (socket/json/wave/argparse). Functions `write_event(sock, etype, data=None, payload=b"")` and `read_event(f)` with the exact Wyoming framing from `scripts/verify-whisper-score.py`; `transcribe_wav(host, port, wav_path) -> dict` streaming 3200-byte chunks; main loop reads the manifest and writes JSONL rows `{"wav", "text", "score"}` (score from the patched transcript event, `None` if absent).
  - `transcribe` stage: for each condition dir under `runs/<run>/processed/<condition>/` **plus** the raw corpus (`condition="raw"` reads `runs/<run>/corpus/`), writes `runs/<run>/transcripts/<backend>/<condition>.jsonl`.

- [ ] **Step 1: Write the failing framing test**

`tests/test_wyoming_framing.py`:
```python
import json
import socket
from stt_eval.wyoming_worker import read_event, write_event


def test_event_roundtrip():
    a, b = socket.socketpair()
    payload = b"\x01\x02" * 1600
    write_event(a, "audio-chunk", {"rate": 16000, "width": 2, "channels": 1}, payload)
    a.close()
    etype, data, got = read_event(b.makefile("rb"))
    assert etype == "audio-chunk"
    assert data["rate"] == 16000
    assert got == payload
```

- [ ] **Step 2: Run to verify it fails, implement wyoming_worker.py**

Run: `uv run pytest tests/test_wyoming_framing.py -v` → FAIL.

`stt_eval/wyoming_worker.py`:
```python
"""Stdlib-only Wyoming STT worker, run inside the compose network via docker run.

Framing mirrors scripts/verify-whisper-score.py (the patched wyoming-whisper adds
`score` to the transcript event).
"""
import argparse
import json
import socket
import wave


def write_event(sock, etype, data=None, payload=b""):
    data_bytes = json.dumps(data or {}).encode()
    header = {
        "type": etype,
        "version": "1.0.0",
        "data_length": len(data_bytes),
        "payload_length": len(payload),
    }
    sock.sendall(json.dumps(header).encode() + b"\n" + data_bytes + payload)


def read_event(f):
    line = f.readline()
    if not line:
        return None
    header = json.loads(line)
    data = header.get("data") or {}
    if header.get("data_length"):
        data = json.loads(f.read(header["data_length"]))
    payload = f.read(header["payload_length"]) if header.get("payload_length") else b""
    return header["type"], data, payload


def transcribe_wav(host, port, wav_path):
    with wave.open(wav_path, "rb") as w:
        assert (w.getframerate(), w.getsampwidth(), w.getnchannels()) == (16000, 2, 1), wav_path
        pcm = w.readframes(w.getnframes())
    fmt = {"rate": 16000, "width": 2, "channels": 1, "timestamp": 0}
    with socket.create_connection((host, port), timeout=300) as s:
        f = s.makefile("rb")
        write_event(s, "transcribe", {"language": "es"})
        write_event(s, "audio-start", fmt)
        for i in range(0, len(pcm), 3200):
            write_event(s, "audio-chunk", fmt, pcm[i:i + 3200])
        write_event(s, "audio-stop", {"timestamp": 0})
        while True:
            evt = read_event(f)
            if evt is None:
                return {"text": "", "score": None}
            etype, data, _ = evt
            if etype == "transcript":
                return {"text": data.get("text", ""), "score": data.get("score")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="wyoming-whisper")
    ap.add_argument("--port", type=int, default=10300)
    ap.add_argument("--manifest", required=True, help="jsonl of {'wav': path}")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    with open(args.manifest, encoding="utf-8") as fin, open(args.out, "w", encoding="utf-8") as fout:
        for line in fin:
            wav = json.loads(line)["wav"]
            row = transcribe_wav(args.host, args.port, wav)
            row["wav"] = wav
            fout.write(json.dumps(row, ensure_ascii=False) + "\n")
            print(wav, "->", row["text"][:60], flush=True)


if __name__ == "__main__":
    main()
```

Run: `uv run pytest tests/test_wyoming_framing.py -v` → PASS.

- [ ] **Step 3: Implement backends.py and register the stage**

`stt_eval/backends.py`:
```python
import json
import math
import subprocess
import tempfile
from pathlib import Path

from tqdm import tqdm


def _done_wavs(out_jsonl: Path) -> set[str]:
    if not out_jsonl.exists():
        return set()
    with out_jsonl.open(encoding="utf-8") as f:
        return {json.loads(line)["wav"] for line in f if line.strip()}


def _medium(wavs: list[Path], out_jsonl: Path) -> None:
    from faster_whisper import WhisperModel
    model = WhisperModel("medium", device="auto", compute_type="int8")
    with out_jsonl.open("a", encoding="utf-8") as f:
        for wav in tqdm(wavs, desc=f"medium->{out_jsonl.stem}"):
            segments, _ = model.transcribe(str(wav), language="es", beam_size=5)
            segs = list(segments)
            text = " ".join(s.text.strip() for s in segs)
            score = math.exp(sum(s.avg_logprob for s in segs) / len(segs)) if segs else None
            f.write(json.dumps({"wav": str(wav), "text": text, "score": score}, ensure_ascii=False) + "\n")


def _jackbot_network() -> str:
    out = subprocess.run(["docker", "network", "ls", "--format", "{{.Name}}"],
                         capture_output=True, text=True, check=True).stdout.split()
    nets = [n for n in out if "jackbot" in n]
    if not nets:
        raise SystemExit("no jackbot docker network - is the compose stack up?")
    return nets[0]


def _wyoming(wavs: list[Path], out_jsonl: Path) -> None:
    # All wavs must live under one root (the run dir) so a single -v mount covers them.
    root = Path(*__common(wavs))
    with tempfile.TemporaryDirectory(dir=root) as td:
        tdp = Path(td)
        with (tdp / "in.jsonl").open("w", encoding="utf-8") as f:
            for wav in wavs:
                f.write(json.dumps({"wav": f"/work/{wav.resolve().relative_to(root.resolve())}"}) + "\n")
        worker = Path(__file__).resolve().parent
        subprocess.run([
            "docker", "run", "--rm", "--network", _jackbot_network(),
            "-v", f"{root.resolve()}:/work", "-v", f"{worker}:/s:ro",
            "python:3.12-slim", "python", "/s/wyoming_worker.py",
            "--manifest", f"/work/{tdp.name}/in.jsonl", "--out", f"/work/{tdp.name}/out.jsonl",
        ], check=True)
        with (tdp / "out.jsonl").open(encoding="utf-8") as fin, out_jsonl.open("a", encoding="utf-8") as fout:
            for line in fin:
                row = json.loads(line)
                row["wav"] = str(root.resolve() / Path(row["wav"]).relative_to("/work"))
                fout.write(json.dumps(row, ensure_ascii=False) + "\n")


def __common(wavs: list[Path]) -> tuple[str, ...]:
    import os
    return Path(os.path.commonpath([w.resolve() for w in wavs])).parts


def transcribe_files(backend: str, wavs: list[Path], out_jsonl: Path) -> None:
    out_jsonl.parent.mkdir(parents=True, exist_ok=True)
    done = _done_wavs(out_jsonl)
    todo = [w for w in wavs if str(w) not in done and str(w.resolve()) not in done]
    if not todo:
        print(f"{out_jsonl}: complete ({len(done)} rows)")
        return
    {"medium": _medium, "wyoming": _wyoming}[backend](todo, out_jsonl)
```

`__main__.py` — stage args (`if name == "transcribe":` → `p.add_argument("--backend", choices=["medium", "wyoming"], required=True)`, `p.add_argument("--conditions", default="raw")` comma-list) and:
```python
def _transcribe(args: argparse.Namespace) -> None:
    from .backends import transcribe_files
    run_dir = Path("runs") / args.run
    for cond in args.conditions.split(","):
        wav_dir = run_dir / "corpus" if cond == "raw" else run_dir / "processed" / cond
        wavs = sorted(wav_dir.glob("*.wav"))
        if not wavs:
            raise SystemExit(f"no wavs in {wav_dir}")
        transcribe_files(args.backend, wavs, run_dir / "transcripts" / args.backend / f"{cond}.jsonl")


STAGES["transcribe"] = _transcribe
```

- [ ] **Step 4: Smoke-run both backends on the raw corpus**

Prereq: Tasks 5–6 done; run `uv run python -m stt_eval mix` first (real corpus). Then:

Run: `uv run python -m stt_eval transcribe --backend medium --conditions raw`
Expected: first run downloads the medium CT2 model (~1.5 GB); JSONL grows to one row per corpus wav; re-run prints `complete`.

Run (compose stack up): `uv run python -m stt_eval transcribe --backend wyoming --conditions raw`
Expected: docker pulls `python:3.12-slim` if needed; per-wav progress lines; `transcripts/wyoming/raw.jsonl` complete. Spanish text visible in rows.

- [ ] **Step 5: Commit**

```bash
git add stt_eval/backends.py stt_eval/wyoming_worker.py stt_eval/__main__.py tests/test_wyoming_framing.py
git commit -m "feat(stt-eval): faster-whisper and wyoming transcription backends"
```

---

### Task 8: `validate` stage (clean-take reference gate)

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/validate_stage.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Test: `scripts/stt-enhancement-eval/tests/test_validate_stage.py`

**Interfaces:**
- Consumes: `transcribe_files("medium", ...)`, `phrase_for_take`, `normalize`.
- Produces: `run_validate(voices_dir: Path, run_dir: Path, max_clean_wer: float = 0.3) -> None` → `runs/<run>/takes.jsonl` rows `{"speaker", "take", "wav", "reference", "clean_wer", "included"}`. A take whose whisper-medium transcript of the **clean** recording deviates from its scripted phrase by WER > 0.3 (truncated 5 s window, misread, wrong mapping) is excluded from the corpus. Task 5's `mix` already consumes this file.
- The WER computation lives in a pure helper `clean_wer(reference: str, hypothesis: str) -> float` (normalize both, `jiwer.wer`).

- [ ] **Step 1: Write the failing test (pure part)**

`tests/test_validate_stage.py`:
```python
from stt_eval.validate_stage import clean_wer


def test_exact_match_zero():
    assert clean_wer("¿Qué tiempo va a hacer mañana?", "qué tiempo va a hacer mañana") == 0.0


def test_truncated_take_scores_high():
    ref = "Pon un temporizador de diez minutos para la pasta que está al fuego."
    hyp = "Pon un temporizador de diez"
    assert clean_wer(ref, hyp) > 0.3
```

- [ ] **Step 2: Run to verify it fails, implement**

Run: `uv run pytest tests/test_validate_stage.py -v` → FAIL.

`stt_eval/validate_stage.py`:
```python
"""Gates each enrollment take: reference is trustworthy only if whisper-medium agrees on clean audio."""
import json
from pathlib import Path

import jiwer

from .backends import transcribe_files
from .phrases import phrase_for_take
from .textnorm import normalize


def clean_wer(reference: str, hypothesis: str) -> float:
    return jiwer.wer(normalize(reference), normalize(hypothesis))


def run_validate(voices_dir: Path, run_dir: Path, max_clean_wer: float = 0.3) -> None:
    takes = sorted(voices_dir.glob("*/enroll-*.wav"))
    if not takes:
        raise SystemExit(f"no takes under {voices_dir} - run fetch first")
    out_jsonl = run_dir / "clean_transcripts.jsonl"
    transcribe_files("medium", takes, out_jsonl)
    by_wav = {}
    with out_jsonl.open(encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            by_wav[Path(row["wav"]).name + "|" + Path(row["wav"]).parent.name] = row["text"]
    run_dir.mkdir(parents=True, exist_ok=True)
    n_excluded = 0
    with (run_dir / "takes.jsonl").open("w", encoding="utf-8") as f:
        for wav in takes:
            speaker, take = wav.parent.name, int(wav.stem.split("-")[1])
            ref = phrase_for_take(take)
            hyp = by_wav.get(wav.name + "|" + speaker, "")
            wer = clean_wer(ref, hyp)
            included = wer <= max_clean_wer
            n_excluded += not included
            f.write(json.dumps({"speaker": speaker, "take": take, "wav": str(wav),
                                "reference": ref, "clean_wer": round(wer, 3),
                                "included": included}, ensure_ascii=False) + "\n")
    print(f"takes: {len(takes)}, excluded (clean WER > {max_clean_wer}): {n_excluded}")
```

`__main__.py`:
```python
def _validate(args: argparse.Namespace) -> None:
    from .validate_stage import run_validate
    run_validate(Path(args.voices), Path("runs") / args.run)


STAGES["validate"] = _validate
```

Run: `uv run pytest tests/test_validate_stage.py -v` → PASS.

- [ ] **Step 3: Smoke-run on real takes, inspect exclusions**

Run: `uv run python -m stt_eval validate && cat runs/round1/takes.jsonl`
Expected: one row per take; spot-check that excluded rows (if any) really are truncated/misread takes — print their `clean_wer` and listen to one if surprising. **If more than ~30 % of takes are excluded, stop and revisit the mapping before proceeding** (wrong mapping looks like uniform exclusion, truncation looks take-specific).
Then rebuild the corpus honoring exclusions: `uv run python -m stt_eval mix` (delete `runs/round1/corpus` + `manifest.jsonl` first so it regenerates, and delete `runs/round1/transcripts/` since ids changed).

- [ ] **Step 4: Commit**

```bash
git add stt_eval/validate_stage.py stt_eval/__main__.py tests/test_validate_stage.py
git commit -m "feat(stt-eval): clean-take reference validation gate"
```

---

### Task 9: `process` stage — GTCRN and DFN3 conditions

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/conditions/__init__.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/conditions/gtcrn.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/conditions/dfn3.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Test: `scripts/stt-enhancement-eval/tests/test_gtcrn_stft.py`

**Interfaces:**
- Consumes: `data/models/gtcrn_simple.onnx` (Task 6).
- Produces:
  - `conditions/__init__.py`: `PROCESSORS: dict[str, Callable[[Path, Path, Path], None]]` mapping condition name → `process(model_dir: Path, wav_in: Path, wav_out: Path)`.
  - `conditions/gtcrn.py`: `process(...)` — streaming ONNX inference: 512-FFT / 256-hop sqrt-Hann STFT frame loop, cache tensors auto-initialized to zeros from the session's input shapes (names introspected, **not** hardcoded), overlap-add resynthesis. Also exposes `stft_frames(audio) -> np.ndarray` and `overlap_add(frames) -> np.ndarray` as pure helpers.
  - `conditions/dfn3.py`: `process(...)` — `df.enhance` API (`init_df()` once per process, module-level lazy), 16 k→48 k handled by `load_audio(..., sr=df_state.sr())`, output resampled back to 16 k via soxr, written PCM_16.
  - `process` stage: `--conditions gtcrn,dfn3` — for each manifest row, `runs/<run>/processed/<cond>/<id>.wav`; skip existing files (resumable).

- [ ] **Step 1: Write the failing STFT round-trip test**

`tests/test_gtcrn_stft.py`:
```python
import numpy as np
from stt_eval.conditions.gtcrn import overlap_add, stft_frames


def test_stft_ola_roundtrip_is_identity():
    rng = np.random.default_rng(0)
    audio = rng.normal(0, 0.1, 16000).astype(np.float32)
    frames = stft_frames(audio)
    assert frames.shape[1] == 257
    rec = overlap_add(np.fft.irfft(frames, n=512).astype(np.float32))
    # sqrt-hann analysis+synthesis at 50% overlap satisfies COLA -> reconstruction
    # matches except the first/last half-window edges.
    assert np.allclose(rec[256:-512], audio[256:-512], atol=1e-4)
```

- [ ] **Step 2: Run to verify it fails, implement gtcrn.py**

Run: `uv run pytest tests/test_gtcrn_stft.py -v` → FAIL.

`stt_eval/conditions/gtcrn.py`:
```python
"""Streaming GTCRN inference (gtcrn_simple.onnx from Xiaobin-Rong/gtcrn stream/)."""
from pathlib import Path

import numpy as np
import soundfile as sf

N_FFT, HOP = 512, 256
_WIN = np.sqrt(np.hanning(N_FFT + 1)[:-1]).astype(np.float32)
_SESSION = None


def stft_frames(audio: np.ndarray) -> np.ndarray:
    pad = (-len(audio)) % HOP
    audio = np.pad(audio, (N_FFT - HOP, pad))
    n_frames = (len(audio) - N_FFT) // HOP + 1
    frames = np.stack([audio[i * HOP:i * HOP + N_FFT] * _WIN for i in range(n_frames)])
    return np.fft.rfft(frames, axis=1)


def overlap_add(frames: np.ndarray) -> np.ndarray:
    out = np.zeros((len(frames) - 1) * HOP + N_FFT, dtype=np.float32)
    for i, frame in enumerate(frames):
        out[i * HOP:i * HOP + N_FFT] += frame * _WIN
    return out[N_FFT - HOP:]


def _session(model_path: Path):
    global _SESSION
    if _SESSION is None:
        import onnxruntime as ort
        _SESSION = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    return _SESSION


def process(model_dir: Path, wav_in: Path, wav_out: Path) -> None:
    sess = _session(model_dir / "gtcrn_simple.onnx")
    ins = sess.get_inputs()
    out_names = [o.name for o in sess.get_outputs()]
    caches = {
        i.name: np.zeros([d if isinstance(d, int) else 1 for d in i.shape], dtype=np.float32)
        for i in ins[1:]
    }
    audio, sr = sf.read(wav_in, dtype="float32")
    assert sr == 16000, wav_in
    spec = stft_frames(audio)
    enhanced = []
    for frame in spec:
        feed = {ins[0].name: np.stack([frame.real, frame.imag], axis=-1)[None, :, None, :].astype(np.float32)}
        feed.update(caches)
        outs = sess.run(out_names, feed)
        enh = outs[0].squeeze()  # (257, 2)
        enhanced.append(enh[:, 0] + 1j * enh[:, 1])
        for name, value in zip(out_names[1:], outs[1:]):
            caches[[i.name for i in ins[1:]][out_names[1:].index(name)]] = value
    frames_td = np.fft.irfft(np.stack(enhanced), n=N_FFT).astype(np.float32)
    rec = overlap_add(frames_td)[:len(audio)]
    sf.write(wav_out, rec, 16000, subtype="PCM_16")
```

Note for the implementer: the cache-name pairing assumes the model's cache outputs are ordered the same as its cache inputs (true for the official export: `conv_cache`, `tra_cache`, `inter_cache`). After first real run, `print([i.name for i in ins], out_names)` once and verify the pairing visually; if names differ only by prefix, match by suffix instead. The smoke test in Step 5 catches a wrong pairing (output audio would be garbage/NaN).

Run: `uv run pytest tests/test_gtcrn_stft.py -v` → PASS.

- [ ] **Step 3: Implement dfn3.py and the PROCESSORS registry**

`stt_eval/conditions/dfn3.py`:
```python
"""DeepFilterNet3 via the deepfilternet pip package (48 kHz round-trip)."""
from pathlib import Path

import soundfile as sf
import soxr

_MODEL = None


def _get_model():
    global _MODEL
    if _MODEL is None:
        from df.enhance import init_df
        _MODEL = init_df()  # (model, df_state, _)
    return _MODEL


def process(model_dir: Path, wav_in: Path, wav_out: Path) -> None:
    from df.enhance import enhance, load_audio
    model, df_state, _ = _get_model()
    audio, _meta = load_audio(str(wav_in), sr=df_state.sr())
    enhanced = enhance(model, df_state, audio).squeeze().numpy()
    out16 = soxr.resample(enhanced, df_state.sr(), 16000)
    sf.write(wav_out, out16, 16000, subtype="PCM_16")
```

`stt_eval/conditions/__init__.py`:
```python
from collections.abc import Callable
from pathlib import Path

from . import dfn3, gtcrn

PROCESSORS: dict[str, Callable[[Path, Path, Path], None]] = {
    "gtcrn": gtcrn.process,
    "dfn3": dfn3.process,
}
```

`__main__.py` — args (`if name == "process":` → `p.add_argument("--conditions", default="gtcrn,dfn3")`) and:
```python
def _process(args: argparse.Namespace) -> None:
    from tqdm import tqdm
    from .conditions import PROCESSORS
    from .manifest import read_manifest
    run_dir = Path("runs") / args.run
    rows = read_manifest(run_dir / "manifest.jsonl")
    for cond in args.conditions.split(","):
        proc = PROCESSORS[cond]
        out_dir = run_dir / "processed" / cond
        out_dir.mkdir(parents=True, exist_ok=True)
        for row in tqdm(rows, desc=cond):
            out = out_dir / f"{row.id}.wav"
            if not out.exists():
                proc(Path(args.data) / "models", run_dir / row.wav, out)


STAGES["process"] = _process
```

- [ ] **Step 4: Run full test suite**

Run: `uv run pytest -v`
Expected: PASS (dfn3/registry have no unit tests — model code is smoke-verified next).

- [ ] **Step 5: Smoke-run both conditions on the real corpus**

Run: `uv run python -m stt_eval process --conditions gtcrn,dfn3`
Expected: first DFN3 call downloads its checkpoint (~50 MB); `processed/gtcrn/` and `processed/dfn3/` fill with one wav per manifest row. Verify per condition on one low-SNR file:
```bash
uv run python - <<'EOF'
import numpy as np, soundfile as sf, glob
for cond in ("gtcrn", "dfn3"):
    f = sorted(glob.glob(f"runs/round1/processed/{cond}/*speech-snr-05*.wav"))[0]
    raw = sf.read(f.replace(f"processed/{cond}", "corpus"))[0]
    enh = sf.read(f)[0]
    assert np.all(np.isfinite(enh)) and len(enh) >= len(raw) - 512
    print(cond, "rms raw", np.sqrt((raw**2).mean()).round(4), "-> enh", np.sqrt((enh**2).mean()).round(4))
EOF
```
Expected: finite audio, enhanced RMS < raw RMS (interference removed). Listen to one pair if in doubt.

- [ ] **Step 6: Commit**

```bash
git add stt_eval/conditions/ stt_eval/__main__.py tests/test_gtcrn_stft.py
git commit -m "feat(stt-eval): gtcrn and dfn3 processing conditions"
```

---

### Task 10: TSE condition (WeSep checkpoint spike)

This task is exploratory by design (the spec flags checkpoint availability as the risk). The deliverable is still concrete: a working `tse` entry in `PROCESSORS` or a documented fallback decision.

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/conditions/tse.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/conditions/__init__.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py` (only if new flags needed)

**Interfaces:**
- Consumes: `data/voices/<speaker>/enroll-*.wav` (enrollment audio), manifest rows (speaker + take of each utterance).
- Produces: `tse.process(model_dir: Path, wav_in: Path, wav_out: Path)` registered as `PROCESSORS["tse"]`. Enrollment rule (no leakage): condition on the concatenation of the speaker's takes **excluding** the take encoded in the filename (`<speaker>-t<take>-...`). Signature stays uniform — `tse.py` parses speaker/take from `wav_in.name` and locates `data/voices` relative to `model_dir.parent`.

- [ ] **Step 1: Pull the WeSep demo checkpoint**

```bash
uv run python - <<'EOF'
from huggingface_hub import snapshot_download
p = snapshot_download("wenet-e2e/wesep-tse-2speaker-demo", repo_type="space", local_dir="data/models/wesep-demo")
print(p)
EOF
ls -R data/models/wesep-demo | head -40
```
Read the space's `app.py`/`requirements.txt`: identify (a) the checkpoint file(s), (b) the inference entry point (how it loads the model and calls extraction with mixture + enrollment wav), (c) expected sample rate. If it imports a `wesep` package, `git clone https://github.com/wenet-e2e/wesep data/models/wesep-src` and add it to `sys.path` inside `tse.py` (do **not** add it to pyproject — it's a spike dependency pinned by clone).

- [ ] **Step 2: Reproduce the demo's inference on one fixture**

Adapt the space's inference call in a throwaway script: input = one real `*-speech-snr+00*.wav` from the corpus + enrollment concat of the same speaker's other takes; output wav. Acceptance: output is intelligibly the target speaker (listen), finite, ~same length. If the checkpoint can't be made to run in ≤ a few hours of effort, **stop and fall back** in this order, applying the same acceptance test: (1) USEF-TSE released checkpoints (github.com/ZBang/USEF-TSE); (2) ClearerVoice-Studio `MossFormer2_SS_16K` blind separation + pick-the-output-closest-to-enrollment via any bundled speaker encoder. Record the choice and why in the results doc (Task 12).

- [ ] **Step 3: Wire it as `conditions/tse.py`**

Structure (final imports depend on Step 2's findings — the enrollment/caching logic is fixed):
```python
"""Target-speaker extraction condition (WeSep demo checkpoint; see Task 10 spike notes)."""
import re
from functools import lru_cache
from pathlib import Path

import numpy as np
import soundfile as sf

_ID_RE = re.compile(r"^(?P<speaker>.+)-t(?P<take>\d+)-")


@lru_cache(maxsize=None)
def _enrollment(voices_dir: str, speaker: str, exclude_take: int) -> np.ndarray:
    parts = [sf.read(p, dtype="float32")[0]
             for p in sorted(Path(voices_dir).glob(f"{speaker}/enroll-*.wav"))
             if int(p.stem.split("-")[1]) != exclude_take]
    assert parts, f"no enrollment takes for {speaker} besides t{exclude_take}"
    return np.concatenate(parts)


def process(model_dir: Path, wav_in: Path, wav_out: Path) -> None:
    m = _ID_RE.match(wav_in.name)
    assert m, wav_in.name
    enroll = _enrollment(str(model_dir.parent / "voices"), m["speaker"], int(m["take"]))
    mixture, sr = sf.read(wav_in, dtype="float32")
    assert sr == 16000
    extracted = _extract(model_dir, mixture, enroll)  # from the Step 2 spike
    sf.write(wav_out, extracted[:len(mixture)], 16000, subtype="PCM_16")
```
Register: add `"tse": tse.process` to `PROCESSORS` (import guarded so `gtcrn,dfn3` runs don't need torch-heavy TSE imports: do the heavy imports inside `_extract`, not module level).

- [ ] **Step 4: Smoke-run the tse condition**

Run: `uv run python -m stt_eval process --conditions tse`
Expected: `processed/tse/` fills (slowest condition — CPU torch; corpus is small, let it run). Same finite/RMS sanity check as Task 9 Step 5 with `cond="tse"`, plus: on a `speech-snr+00` file, listen and confirm the competing speaker is attenuated. **This is the one condition where the denoisers should fail and TSE should not** — worth 2 minutes of actual listening.

- [ ] **Step 5: Commit**

```bash
git add stt_eval/conditions/tse.py stt_eval/conditions/__init__.py stt_eval/__main__.py
git commit -m "feat(stt-eval): target-speaker-extraction condition (wesep checkpoint)"
```

---

### Task 11: `report` stage

**Files:**
- Create: `scripts/stt-enhancement-eval/stt_eval/report_stage.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Test: `scripts/stt-enhancement-eval/tests/test_report_stage.py`

**Interfaces:**
- Consumes: `runs/<run>/manifest.jsonl`, `runs/<run>/transcripts/<backend>/<condition>.jsonl`.
- Produces:
  - Pure: `score_rows(manifest: list[Utterance], transcripts: dict[str, tuple[str, float | None]]) -> list[dict]` — one dict per utterance: `{id, speaker, interference, snr_db, wer, cer, score, ref, hyp}` (normalized before jiwer; transcripts keyed by wav **basename**, value = (text, confidence score)).
  - Pure: `decision(cells: list[dict]) -> dict` — for one condition vs a raw baseline list: `{"rel_improvement_low_snr": float, "clean_wer_delta": float, "passes": bool}` implementing the spec rule (≥20 % relative WER cut pooled over speech-interference cells with SNR ≤ 5 dB; clean delta ≤ +0.02 absolute).
  - `run_report(run_dir: Path) -> None` — discovers backends/conditions from the transcripts tree, writes `runs/<run>/report.md` (model × SNR WER tables per interference type per backend + decision block + caveats section listing excluded takes and the digits-vs-words normalization note) and `runs/<run>/per_utterance.csv` (columns: id, speaker, interference, snr_db, condition, backend, wer, cer, score, ref, hyp).

- [ ] **Step 1: Write the failing tests**

`tests/test_report_stage.py`:
```python
from stt_eval.manifest import Utterance
from stt_eval.report_stage import decision, score_rows


def _u(uid, interference, snr):
    return Utterance(id=uid, speaker="s", take=1, wav=f"corpus/{uid}.wav",
                     reference="pon un temporizador de diez minutos", interference=interference, snr_db=snr)


def test_score_rows_computes_wer_and_keeps_score():
    man = [_u("a", "speech", 0.0)]
    rows = score_rows(man, {"a.wav": ("pon un temporizador de veinte minutos", 0.62)})
    assert abs(rows[0]["wer"] - 1 / 6) < 1e-9
    assert rows[0]["score"] == 0.62


def test_decision_rule():
    raw = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.8},
        {"interference": "speech", "snr_db": 5.0, "wer": 0.5},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.1},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    good = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.4},
        {"interference": "speech", "snr_db": 5.0, "wer": 0.3},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.1},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    d = decision(raw_cells=raw, model_cells=good)
    assert d["passes"] and d["rel_improvement_low_snr"] > 0.2
    bad_clean = [dict(r) for r in good]
    bad_clean[-1]["wer"] = 0.12  # clean regression
    assert not decision(raw_cells=raw, model_cells=bad_clean)["passes"]
```

- [ ] **Step 2: Run to verify it fails, implement**

Run: `uv run pytest tests/test_report_stage.py -v` → FAIL.

`stt_eval/report_stage.py`:
```python
"""Joins manifests with transcripts, emits WER tables, CSV, and the decision block."""
import csv
import json
from pathlib import Path

import jiwer

from .manifest import Utterance, read_manifest
from .textnorm import normalize

LOW_SNR_MAX = 5.0
MIN_REL_IMPROVEMENT = 0.20
MAX_CLEAN_DELTA = 0.02


def score_rows(manifest: list[Utterance], transcripts: dict[str, tuple[str, float | None]]) -> list[dict]:
    rows = []
    for u in manifest:
        hyp, score = transcripts.get(Path(u.wav).name, ("", None))
        ref_n, hyp_n = normalize(u.reference), normalize(hyp)
        rows.append({
            "id": u.id, "speaker": u.speaker, "interference": u.interference,
            "snr_db": u.snr_db,
            "wer": jiwer.wer(ref_n, hyp_n) if ref_n else 0.0,
            "cer": jiwer.cer(ref_n, hyp_n) if ref_n else 0.0,
            "score": score,
            "ref": u.reference, "hyp": hyp,
        })
    return rows


def _mean(xs: list[float]) -> float:
    return sum(xs) / len(xs) if xs else float("nan")


def decision(raw_cells: list[dict], model_cells: list[dict]) -> dict:
    def low_snr(cells):
        return [c["wer"] for c in cells
                if c["interference"] == "speech" and c["snr_db"] is not None and c["snr_db"] <= LOW_SNR_MAX]

    def clean(cells):
        return [c["wer"] for c in cells if c["interference"] == "none"]

    raw_low, model_low = _mean(low_snr(raw_cells)), _mean(low_snr(model_cells))
    rel = (raw_low - model_low) / raw_low if raw_low else 0.0
    delta = _mean(clean(model_cells)) - _mean(clean(raw_cells))
    return {
        "rel_improvement_low_snr": rel,
        "clean_wer_delta": delta,
        "passes": rel >= MIN_REL_IMPROVEMENT and delta <= MAX_CLEAN_DELTA,
    }


def _load_transcripts(path: Path) -> dict[str, tuple[str, float | None]]:
    out = {}
    with path.open(encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            out[Path(row["wav"]).name] = (row["text"], row.get("score"))
    return out


def run_report(run_dir: Path) -> None:
    manifest = read_manifest(run_dir / "manifest.jsonl")
    scored: dict[tuple[str, str], list[dict]] = {}
    scores_meta: dict[tuple[str, str], dict[str, float | None]] = {}
    for backend_dir in sorted((run_dir / "transcripts").iterdir()):
        for cond_file in sorted(backend_dir.glob("*.jsonl")):
            key = (backend_dir.name, cond_file.stem)
            scored[key] = score_rows(manifest, _load_transcripts(cond_file))

    lines = ["# STT enhancement eval report", ""]
    snrs = sorted({u.snr_db for u in manifest if u.snr_db is not None}, reverse=True)
    for backend in sorted({b for b, _ in scored}):
        conds = sorted({c for b, c in scored if b == backend})
        for interference in ("speech", "music"):
            lines += [f"## backend={backend} interference={interference}", "",
                      "| SNR (dB) | " + " | ".join(conds) + " |",
                      "|---" * (len(conds) + 1) + "|"]
            for snr in [None] + snrs:
                label = "clean" if snr is None else f"{snr:+.0f}"
                cells = []
                for cond in conds:
                    sel = [r["wer"] for r in scored[(backend, cond)]
                           if (r["interference"] == ("none" if snr is None else interference))
                           and r["snr_db"] == snr]
                    cells.append(f"{100 * _mean(sel):.1f}" if sel else "-")
                lines.append(f"| {label} | " + " | ".join(cells) + " |")
            lines.append("")
        if "raw" in conds:
            lines.append(f"### Decision (backend={backend}, rule: ≥{MIN_REL_IMPROVEMENT:.0%} relative WER cut at SNR ≤ {LOW_SNR_MAX:.0f} dB speech, clean delta ≤ {MAX_CLEAN_DELTA})")
            for cond in conds:
                if cond == "raw":
                    continue
                d = decision(scored[(backend, "raw")], scored[(backend, cond)])
                lines.append(f"- **{cond}**: rel low-SNR improvement {d['rel_improvement_low_snr']:+.1%}, "
                             f"clean ΔWER {d['clean_wer_delta']:+.3f} → "
                             f"{'PASS' if d['passes'] else 'FAIL'}")
            conf = []
            for cond in conds:
                s = [r["score"] for r in scored[(backend, cond)]
                     if r["score"] is not None and r["interference"] == "speech"
                     and r["snr_db"] is not None and r["snr_db"] <= LOW_SNR_MAX]
                conf.append(f"{cond}={_mean(s):.2f}" if s else f"{cond}=-")
            lines.append(f"- mean confidence (speech ≤{LOW_SNR_MAX:.0f} dB): " + ", ".join(conf))
            lines.append("")
    takes_file = run_dir / "takes.jsonl"
    lines += ["## Caveats", "- References use scripted phrases; digit-vs-word mismatches (e.g. 'diez' vs '10') inflate all conditions equally.",]
    if takes_file.exists():
        excluded = [json.loads(line) for line in takes_file.open(encoding="utf-8")]
        names = [t["speaker"] + "-t" + str(t["take"]) for t in excluded if not t["included"]]
        lines.append(f"- Excluded takes (clean WER > 0.3): {names or 'none'}")
    (run_dir / "report.md").write_text("\n".join(lines), encoding="utf-8")

    with (run_dir / "per_utterance.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["id", "speaker", "interference", "snr_db", "condition", "backend", "wer", "cer", "score", "ref", "hyp"])
        for (backend, cond), rows in scored.items():
            for r in rows:
                w.writerow([r["id"], r["speaker"], r["interference"], r["snr_db"], cond, backend,
                            f"{r['wer']:.4f}", f"{r['cer']:.4f}", r["score"], r["ref"], r["hyp"]])
    print(f"wrote {run_dir / 'report.md'} and per_utterance.csv")
```

`__main__.py`:
```python
def _report(args: argparse.Namespace) -> None:
    from .report_stage import run_report
    run_report(Path("runs") / args.run)


STAGES["report"] = _report
```

Run: `uv run pytest tests/test_report_stage.py -v` → PASS. Then `uv run pytest -v` → all PASS.

- [ ] **Step 3: Commit**

```bash
git add stt_eval/report_stage.py stt_eval/__main__.py tests/test_report_stage.py
git commit -m "feat(stt-eval): WER report stage with decision rule"
```

---

### Task 12: Round-1 sweep + results doc

**Files:**
- Create: `scripts/stt-enhancement-eval/results/2026-07-round1.md` (copied+annotated report)
- Create: `scripts/stt-enhancement-eval/README.md`

**Interfaces:**
- Consumes: every prior stage.
- Produces: the round-1 numbers and the committed results doc — the actual deliverable of this whole plan.

- [ ] **Step 1: Run the full pipeline in order**

```bash
uv run python -m stt_eval fetch
uv run python -m stt_eval validate                      # then inspect takes.jsonl
rm -rf runs/round1/corpus runs/round1/manifest.jsonl runs/round1/transcripts
uv run python -m stt_eval mix
uv run python -m stt_eval process --conditions gtcrn,dfn3,tse
uv run python -m stt_eval transcribe --backend medium --conditions raw,gtcrn,dfn3,tse
uv run python -m stt_eval transcribe --backend wyoming --conditions raw,gtcrn,dfn3,tse   # compose stack up
uv run python -m stt_eval report
```
Expected rough scale: with N included takes, corpus = 11 N wavs, ×4 conditions ≈ 44 N transcriptions per backend. Everything is resumable; re-run any stage after interruption.

- [ ] **Step 2: Sanity-check the numbers before believing them**

- Raw WER must degrade monotonically-ish as SNR drops (speech interference worse than music at equal SNR is expected). If raw clean WER is high (>15 %), something upstream is wrong (mapping, normalization, take quality) — investigate before comparing models.
- Spot-read 5 per-utterance rows at `speech snr-05`: does the hyp text contain interference-speaker words in `raw` but not in `tse`?

- [ ] **Step 3: Write the results doc + README**

`results/2026-07-round1.md`: copy `runs/round1/report.md`, prepend: run date, corpus size (speakers/takes/excluded), model+checkpoint identities (incl. which TSE checkpoint the Task 10 spike settled on and why), backend versions (wyoming-whisper image tag, faster-whisper medium), and a **Conclusions** section answering: which conditions PASS the decision rule per backend, and the recommendation (integrate / don't integrate / proceed to phase-2 field corpus per the spec's gate).

`README.md`: 10 lines — what this harness is, spec link, stage order (the Step 1 block), where results live.

- [ ] **Step 4: Commit**

```bash
git add scripts/stt-enhancement-eval/results/2026-07-round1.md scripts/stt-enhancement-eval/README.md
git commit -m "docs(stt-eval): round-1 synthetic sweep results"
```

---

## Self-Review Notes

- Spec coverage: corpus (T5/T6/T8), conditions (T9/T10), backends (T7), metrics/report/decision (T11), phase-1 execution (T12). Phase-2 field corpus is explicitly out of scope per spec.
- Type consistency: `Utterance` fields, `PROCESSORS` signature `(model_dir, wav_in, wav_out)`, transcript JSONL `{"wav","text","score"}`, takes.jsonl `{"speaker","take","wav","reference","clean_wer","included"}` are used identically across tasks.
- Known judgment points left to the implementer on purpose: GTCRN cache-name pairing (T9 note), WeSep spike fallback chain (T10 Step 2).
