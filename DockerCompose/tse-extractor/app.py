"""Target-speaker extraction sidecar (spec: 2026-07-22-tse-live-integration-design.md).

Loads the WeSep BSRNN+ECAPA checkpoint once at startup and serves one extraction per
request under a lock (utterances arrive seconds apart; the hub enforces the deadline).
Enrollment is the hub's voices volume: /voices/<speaker>/enroll-*.wav, concatenated and
cached, invalidated whenever the directory's (name, size, mtime) signature changes. This
matches the round-1 eval reference (stt_eval/conditions/tse.py) and the enrollment
tooling (scripts/enroll-voice.sh writes enroll-<n>.wav) -- any other .wav dropped in a
speaker's directory (debug captures, partial uploads) is intentionally ignored.
"""
import json
import os
import sys
import tempfile
import threading
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from flask import Flask, Response, request

sys.path.insert(0, os.environ.get("WESEP_SRC", "/opt/wesep-src"))
from wesep.cli.extractor import load_model_local  # noqa: E402

VOICES = Path(os.environ.get("VOICES_DIR", "/voices"))
MODEL_DIR = os.environ.get("MODEL_DIR", "/models/wesep-english")
CACHE = Path(os.environ.get("ENROLL_CACHE", "/tmp/enroll-cache"))

app = Flask(__name__)
lock = threading.Lock()
extractor = load_model_local(MODEL_DIR)
extractor.set_device("cpu")

def _physical_cores():
    """Unique (package, core) pairs from /proc/cpuinfo — SMT siblings collapse to one."""
    try:
        cores, phys, core = set(), None, None
        with open("/proc/cpuinfo") as f:
            for line in f:
                if line.startswith("physical id"):
                    phys = line.split(":", 1)[1].strip()
                elif line.startswith("core id"):
                    core = line.split(":", 1)[1].strip()
                elif not line.strip():
                    if phys is not None and core is not None:
                        cores.add((phys, core))
                    phys = core = None
        return len(cores)
    except OSError:
        return 0


# silero_vad/model.py calls torch.set_num_threads(1) at MODULE level and the wesep import
# chain drags it in, silently single-threading every extraction (measured 11.2s -> 2.7s for
# 8s of audio on a 5900X). Restore parallelism after all imports; 0 = one thread per PHYSICAL
# core — SMT oversubscription measured ~1.6x slower (4.3s vs 2.6s per 8s capture at 24 vs 12
# threads on a 5900X). TSE_TORCH_THREADS pins an explicit count.
_threads = int(os.environ.get("TSE_TORCH_THREADS", "0"))
torch.set_num_threads(_threads if _threads > 0 else (_physical_cores() or os.cpu_count() or 1))


ENROLL_GLOB = "enroll-*.wav"  # matches scripts/enroll-voice.sh output and the round-1 reference


def _speakers():
    if not VOICES.is_dir():
        return []
    return sorted(p.name for p in VOICES.iterdir() if p.is_dir() and any(p.glob(ENROLL_GLOB)))


def _signature(speaker_dir):
    return json.dumps(sorted(
        (f.name, f.stat().st_size, f.stat().st_mtime) for f in speaker_dir.glob(ENROLL_GLOB)))


def _valid_speaker(name):
    """Reject anything that isn't a plain single path segment (no traversal)."""
    return bool(name) and name not in (".", "..") and os.path.basename(name) == name


def _enrollment_wav(speaker):
    """Concatenated enrollment for the speaker (all takes), cached; None if unknown."""
    speaker_dir = VOICES / speaker
    takes = sorted(speaker_dir.glob(ENROLL_GLOB)) if speaker_dir.is_dir() else []
    if not takes:
        return None
    CACHE.mkdir(parents=True, exist_ok=True)
    target = CACHE / f"{speaker}.wav"
    sig_file = CACHE / f"{speaker}.sig"
    sig = _signature(speaker_dir)
    if not (target.exists() and sig_file.exists() and sig_file.read_text() == sig):
        parts = [sf.read(str(t), dtype="float32")[0] for t in takes]
        sf.write(str(target), np.concatenate(parts), 16000, subtype="PCM_16")
        sig_file.write_text(sig)
    return target


@app.get("/health")
def health():
    return {"status": "ready", "speakers": _speakers()}


@app.post("/extract")
def extract():
    speaker = request.args.get("speaker", "")
    if not _valid_speaker(speaker):
        return Response(f"unknown speaker {speaker!r}", status=404)
    enrollment = _enrollment_wav(speaker)
    if enrollment is None:
        return Response(f"unknown speaker {speaker!r}", status=404)
    with tempfile.TemporaryDirectory(prefix="tse-") as td:
        mix = Path(td) / "mix.wav"
        out = Path(td) / "out.wav"
        mix.write_bytes(request.get_data())
        mixture_len = sf.info(str(mix)).frames
        with lock:
            speech = extractor.extract_speech(str(mix), str(enrollment))
        if speech is None:
            return Response("extraction returned no speech", status=422)
        extracted = speech[0].detach().cpu().numpy()[:mixture_len]
        sf.write(str(out), extracted, 16000, subtype="PCM_16")
        return Response(out.read_bytes(), mimetype="audio/wav")


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=9098, threaded=True)
