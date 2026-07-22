"""Target-speaker extraction condition (WeSep BSRNN+ECAPA demo checkpoint; see Task 10 spike notes).

The WeSep TSE stack (wesep + wespeaker + silero-vad) cannot live in the pinned
torch==2.1.2 project venv without risking the DFN3/gtcrn deps, so it runs in an
isolated, gitignored venv under ``data/models/tse-env`` and is driven here as a
persistent subprocess. Layout under ``model_dir`` (== ``data/models``):

    tse-env/       uv venv (torch 2.1.2+cpu, wespeaker@e9bbf73 pinned, silero-vad, ...)
    wesep-src/     git clone of wenet-e2e/wesep (added to sys.path in the worker)
    wesep-english/ bsrnn_ecapa_vox1 checkpoint (avg_model.pt + config.yaml)

The worker loads the 282 MB checkpoint once and then streams one extraction per
request line, so the ~8 s import/load cost is paid once for the whole corpus.
Heavy torch/wesep imports happen only inside the worker process, never in the
project venv, so ``gtcrn,dfn3`` runs stay torch-TSE-free.
"""
import atexit
import re
import subprocess
import tempfile
from functools import lru_cache
from pathlib import Path

import numpy as np
import soundfile as sf

_ID_RE = re.compile(r"^(?P<speaker>.+)-t(?P<take>\d+)-")

# Runs inside data/models/tse-env; argv: wesep_src, wesep_model_dir. Protocol:
# read "<mix_wav>\t<enroll_wav>\t<out_wav>" lines on stdin, write "OK"/"ERR ..."
# lines on stdout. Diagnostics go to stderr (captured to a log by the parent).
_WORKER_SRC = r"""
import sys
sys.path.insert(0, sys.argv[1])
import soundfile as sf
from wesep.cli.extractor import load_model_local

ext = load_model_local(sys.argv[2])
ext.set_device("cpu")
sys.stdout.write("READY\n")
sys.stdout.flush()

for line in sys.stdin:
    line = line.rstrip("\n")
    if not line:
        continue
    try:
        mix_wav, enroll_wav, out_wav = line.split("\t")
        speech = ext.extract_speech(mix_wav, enroll_wav)
        if speech is None:
            raise RuntimeError("extractor returned None (no speech / not joint-training)")
        arr = speech[0].detach().cpu().numpy()
        sf.write(out_wav, arr, 16000, subtype="PCM_16")
        sys.stdout.write("OK\n")
    except Exception as exc:  # noqa: BLE001 - report back, keep the server alive
        sys.stdout.write("ERR " + repr(exc) + "\n")
    sys.stdout.flush()
"""

_WORKERS: dict[str, subprocess.Popen] = {}


def _worker(model_dir: Path) -> subprocess.Popen:
    """Lazily launch (and cache) the persistent extraction subprocess for model_dir."""
    key = str(model_dir)
    proc = _WORKERS.get(key)
    if proc is not None and proc.poll() is None:
        return proc
    python = model_dir / "tse-env" / "bin" / "python"
    wesep_src = model_dir / "wesep-src"
    wesep_model = model_dir / "wesep-english"
    for p in (python, wesep_src, wesep_model / "avg_model.pt"):
        if not p.exists():
            raise FileNotFoundError(f"TSE stack missing: {p} (see Task 10 spike notes)")
    log = open(model_dir / "tse-env" / "worker.log", "w")
    proc = subprocess.Popen(
        [str(python), "-c", _WORKER_SRC, str(wesep_src), str(wesep_model)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True,
    )
    ready = proc.stdout.readline().strip()
    if ready != "READY":
        raise RuntimeError(f"TSE worker failed to start (got {ready!r}); see {log.name}")
    _WORKERS[key] = proc
    atexit.register(_shutdown, proc)
    return proc


def _shutdown(proc: subprocess.Popen) -> None:
    if proc.poll() is None:
        try:
            proc.stdin.close()
            proc.wait(timeout=5)
        except Exception:  # noqa: BLE001
            proc.kill()


def _extract(model_dir: Path, mixture: np.ndarray, enroll: np.ndarray) -> np.ndarray:
    proc = _worker(model_dir)
    with tempfile.TemporaryDirectory(prefix="tse-") as td:
        mix_wav = Path(td) / "mix.wav"
        enroll_wav = Path(td) / "enroll.wav"
        out_wav = Path(td) / "out.wav"
        sf.write(mix_wav, mixture, 16000, subtype="PCM_16")
        sf.write(enroll_wav, enroll, 16000, subtype="PCM_16")
        proc.stdin.write(f"{mix_wav}\t{enroll_wav}\t{out_wav}\n")
        proc.stdin.flush()
        reply = proc.stdout.readline().strip()
        if reply != "OK":
            raise RuntimeError(f"TSE worker error: {reply}")
        extracted, _ = sf.read(out_wav, dtype="float32")
    return extracted


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
    extracted = _extract(model_dir, mixture, enroll)
    sf.write(wav_out, extracted[:len(mixture)], 16000, subtype="PCM_16")
