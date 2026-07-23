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