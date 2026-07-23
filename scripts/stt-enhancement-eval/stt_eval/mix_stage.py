"""Builds the synthetic corpus: enrollment takes x {clean, speech-bed, music-bed} x SNR grid."""
import json
import shutil
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


def _guard_stale_derivatives(run_dir: Path, force: bool) -> None:
    """One RNG threads through the whole take iteration, so a given id's noise bed depends on
    every take mixed before it: any re-mix (new take, changed exclusions, different seed) can
    rewrite corpus wavs whose ids are unchanged. process/transcribe resume by id/path only, so
    they would keep stale outputs for those ids and report would silently join fresh raw audio
    against enhanced audio/transcripts from a different corpus. Re-mixing therefore requires
    --force, which drops the corpus and every stage derived from it."""
    stale = [d for d in (run_dir / "corpus", run_dir / "processed", run_dir / "transcripts")
             if d.exists()]
    if not stale:
        return
    if not force:
        raise SystemExit(
            f"{run_dir} already holds a mixed corpus; re-mixing would desynchronize it from "
            "processed/transcript caches. Re-run with --force to drop them all and re-mix.")
    for d in stale:
        shutil.rmtree(d)
    (run_dir / "manifest.jsonl").unlink(missing_ok=True)


def run_mix(voices_dir: Path, data_dir: Path, run_dir: Path, seed: int, takes_file: Path | None,
            force: bool = False) -> None:
    rng = np.random.default_rng(seed)
    speech_files = sorted((data_dir / "interference/speech").rglob("*.wav"))
    music_files = sorted((data_dir / "interference/music").rglob("*.wav"))
    if not speech_files or not music_files:
        raise SystemExit("interference beds missing - run the fetch stage first")
    _guard_stale_derivatives(run_dir, force)
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
