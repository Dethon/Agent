"""Target-speaker extraction sidecar (spec: 2026-07-22-tse-live-integration-design.md).

Loads the WeSep BSRNN+ECAPA checkpoint once at startup and serves one extraction per
request under a lock (utterances arrive seconds apart; the hub enforces the deadline).
Enrollment is the hub's voices volume: /voices/<speaker>/*.wav, concatenated and cached,
invalidated whenever the directory's (name, size, mtime) signature changes.
"""
import json
import os
import sys
import tempfile
import threading
from pathlib import Path

import numpy as np
import soundfile as sf
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


def _speakers():
    if not VOICES.is_dir():
        return []
    return sorted(p.name for p in VOICES.iterdir() if p.is_dir() and any(p.glob("*.wav")))


def _signature(speaker_dir):
    return json.dumps(sorted(
        (f.name, f.stat().st_size, f.stat().st_mtime) for f in speaker_dir.glob("*.wav")))


def _enrollment_wav(speaker):
    """Concatenated enrollment for the speaker (all takes), cached; None if unknown."""
    speaker_dir = VOICES / speaker
    takes = sorted(speaker_dir.glob("*.wav")) if speaker_dir.is_dir() else []
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
    enrollment = _enrollment_wav(speaker)
    if enrollment is None:
        return Response(f"unknown speaker {speaker!r}", status=404)
    with tempfile.TemporaryDirectory(prefix="tse-") as td:
        mix = Path(td) / "mix.wav"
        out = Path(td) / "out.wav"
        mix.write_bytes(request.get_data())
        with lock:
            speech = extractor.extract_speech(str(mix), str(enrollment))
        if speech is None:
            return Response("extraction returned no speech", status=422)
        sf.write(str(out), speech[0].detach().cpu().numpy(), 16000, subtype="PCM_16")
        return Response(out.read_bytes(), mimetype="audio/wav")


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=9098, threaded=True)
