import numpy as np
from stt_eval.conditions.gtcrn import overlap_add, stft_frames


def test_stft_ola_roundtrip_is_identity():
    rng = np.random.default_rng(0)
    audio = rng.normal(0, 0.1, 16000).astype(np.float32)
    frames = stft_frames(audio)
    assert frames.shape[1] == 257
    # overlap_add's output is right-padded to a hop-multiple (streaming contract);
    # process() truncates the same way before comparing/writing, so mirror that here.
    rec = overlap_add(np.fft.irfft(frames, n=512).astype(np.float32))[:len(audio)]
    # sqrt-hann analysis+synthesis at 50% overlap satisfies COLA -> reconstruction
    # matches except the first/last half-window edges.
    assert np.allclose(rec[256:-512], audio[256:-512], atol=1e-4)
