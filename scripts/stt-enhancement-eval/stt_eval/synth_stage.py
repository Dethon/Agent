"""Builds a short-command corpus by synthesizing each phrase through Lemonade's Kokoro TTS.

SYNTHETIC SPEECH. There is no room, no reverberation, no far-field mic and no speaker variation
beyond the TTS voices, so a WER from this corpus does not transfer to a deployed satellite. What
it is good for is comparing two decode configurations against each other on identical audio —
which is what the prompt and whisper-flag changes need. Any result written from it must say so,
the same way results/2026-07-round1.md carries its synthetic-mixing caveat.

The corpus is clean-only (interference="none", snr_db=None), so `report`'s PASS/FAIL block — a
rule about enhancement at low SNR — is degenerate here. The WER table is the output that matters.
"""
import io
import json
import re
import urllib.request
from collections.abc import Callable
from pathlib import Path

import numpy as np
import soundfile as sf
import soxr

from .manifest import Utterance, write_manifest
from .phrases import SHORT_COMMANDS

TARGET_RATE = 16000

# (phrase, voice) -> wav bytes; the tests swap in a fake for the whole TTS round trip.
Fetch = Callable[[str, str], bytes]


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:40]


def _fetch_speech(base_url: str, model: str) -> Fetch:
    def fetch(text: str, voice: str) -> bytes:
        body = json.dumps(
            {"model": model, "voice": voice, "input": text, "response_format": "wav"}
        ).encode()
        req = urllib.request.Request(
            f"{base_url.rstrip('/')}/api/v1/audio/speech",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=120) as resp:
            return resp.read()

    return fetch


def _to_16k_mono_pcm(wav_bytes: bytes, out_path: Path) -> None:
    # Kokoro returns float32 at 24 kHz; the satellites send 16 kHz mono s16le, and the corpus must
    # match what the hub actually posts or the decode is not being measured on prod-shaped audio.
    samples, rate = sf.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=True)
    mono = samples.mean(axis=1)
    if rate != TARGET_RATE:
        mono = soxr.resample(mono, rate, TARGET_RATE)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    sf.write(out_path, np.clip(mono, -1.0, 1.0), TARGET_RATE, subtype="PCM_16")


def run_synth(
    run_dir: Path,
    base_url: str,
    voices: list[str],
    model: str,
    fetch: Fetch | None = None,
) -> None:
    fetch = fetch or _fetch_speech(base_url, model)
    corpus = run_dir / "corpus"
    rows = [
        _synth_one(corpus, run_dir, fetch, voice, index, phrase)
        for voice in voices
        for index, phrase in enumerate(SHORT_COMMANDS, start=1)
    ]
    write_manifest(run_dir / "manifest.jsonl", rows)


def _synth_one(corpus: Path, run_dir: Path, fetch: Fetch, voice: str, index: int,
               phrase: str) -> Utterance:
    uid = f"{voice}-{index:02d}-{_slug(phrase)}"
    wav = corpus / f"{uid}.wav"
    # Presence-based resume, matching every other stage in this harness.
    if not wav.exists():
        _to_16k_mono_pcm(fetch(phrase, voice), wav)
    return Utterance(
        id=uid,
        speaker=voice,
        take=index,
        wav=str(wav.relative_to(run_dir)),
        reference=phrase,
        interference="none",
        snr_db=None,
    )
