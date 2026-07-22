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
