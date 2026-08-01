import io
import wave

import numpy as np
import soundfile as sf

from stt_eval.manifest import read_manifest
from stt_eval.phrases import SHORT_COMMANDS
from stt_eval.synth_stage import run_synth


def _wav_bytes(seconds=0.5, rate=24000):
    buf = io.BytesIO()
    samples = np.zeros(int(rate * seconds), dtype="float32")
    sf.write(buf, samples, rate, format="WAV", subtype="FLOAT")
    return buf.getvalue()


def test_run_synth_writes_16k_mono_pcm_and_a_manifest(tmp_path):
    calls = []

    def fake_fetch(text, voice):
        calls.append((text, voice))
        return _wav_bytes()

    run_synth(tmp_path, "http://lemonade:13305", ["em_santa", "ef_dora"], "kokoro-v1", fetch=fake_fetch)

    assert len(calls) == len(SHORT_COMMANDS) * 2
    manifest = read_manifest(tmp_path / "manifest.jsonl")
    assert len(manifest) == len(SHORT_COMMANDS) * 2
    assert {u.interference for u in manifest} == {"none"}
    assert all(u.snr_db is None for u in manifest)
    assert all(u.reference in SHORT_COMMANDS for u in manifest)
    assert len({u.id for u in manifest}) == len(manifest)

    for u in manifest:
        with wave.open(str(tmp_path / u.wav), "rb") as w:
            assert w.getframerate() == 16000
            assert w.getnchannels() == 1
            assert w.getsampwidth() == 2


def test_run_synth_is_idempotent(tmp_path):
    run_synth(tmp_path, "http://x", ["em_santa"], "kokoro-v1", fetch=lambda text, voice: _wav_bytes())

    calls = []

    def counting_fetch(text, voice):
        calls.append(text)
        return _wav_bytes()

    run_synth(tmp_path, "http://x", ["em_santa"], "kokoro-v1", fetch=counting_fetch)

    assert calls == []
