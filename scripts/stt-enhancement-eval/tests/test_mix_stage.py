import numpy as np
import pytest
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


# Noise-bed draws depend on every take iterated before a given id, so re-mixing into an
# existing run silently desynchronizes the overwritten corpus from processed/transcript
# artifacts that process/transcribe would then resume as "already done".
def test_remix_without_force_refuses_when_corpus_exists(tmp_path: Path):
    voices, data, run = _setup(tmp_path)
    run_mix(voices, data, run, seed=7, takes_file=None)
    with pytest.raises(SystemExit, match="--force"):
        run_mix(voices, data, run, seed=7, takes_file=None)


def test_remix_with_force_clears_stale_derived_stages(tmp_path: Path):
    voices, data, run = _setup(tmp_path)
    run_mix(voices, data, run, seed=7, takes_file=None)
    stale_processed = run / "processed/gtcrn/fran-t1-music-snr+05.wav"
    stale_processed.parent.mkdir(parents=True)
    stale_processed.write_bytes(b"stale")
    stale_transcripts = run / "transcripts/medium/raw.jsonl"
    stale_transcripts.parent.mkdir(parents=True)
    stale_transcripts.write_text("{}\n")

    run_mix(voices, data, run, seed=7, takes_file=None, force=True)

    assert not stale_processed.exists()
    assert not stale_transcripts.exists()
    assert len(read_manifest(run / "manifest.jsonl")) == 2 * (1 + 2 * 5)
