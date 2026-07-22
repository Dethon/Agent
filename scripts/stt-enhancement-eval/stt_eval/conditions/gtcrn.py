"""Streaming GTCRN inference (gtcrn_simple.onnx from Xiaobin-Rong/gtcrn stream/)."""
from pathlib import Path

import numpy as np
import soundfile as sf

N_FFT, HOP = 512, 256
_WIN = np.sqrt(np.hanning(N_FFT + 1)[:-1]).astype(np.float32)
_SESSION = None


def stft_frames(audio: np.ndarray) -> np.ndarray:
    pad = (-len(audio)) % HOP
    audio = np.pad(audio, (N_FFT - HOP, pad))
    n_frames = (len(audio) - N_FFT) // HOP + 1
    frames = np.stack([audio[i * HOP:i * HOP + N_FFT] * _WIN for i in range(n_frames)])
    return np.fft.rfft(frames, axis=1)


def overlap_add(frames: np.ndarray) -> np.ndarray:
    out = np.zeros((len(frames) - 1) * HOP + N_FFT, dtype=np.float32)
    for i, frame in enumerate(frames):
        out[i * HOP:i * HOP + N_FFT] += frame * _WIN
    return out[N_FFT - HOP:]


def _session(model_path: Path):
    global _SESSION
    if _SESSION is None:
        import onnxruntime as ort
        _SESSION = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    return _SESSION


def process(model_dir: Path, wav_in: Path, wav_out: Path) -> None:
    sess = _session(model_dir / "gtcrn_simple.onnx")
    ins = sess.get_inputs()
    out_names = [o.name for o in sess.get_outputs()]
    caches = {
        i.name: np.zeros([d if isinstance(d, int) else 1 for d in i.shape], dtype=np.float32)
        for i in ins[1:]
    }
    audio, sr = sf.read(wav_in, dtype="float32")
    assert sr == 16000, wav_in
    spec = stft_frames(audio)
    enhanced = []
    for frame in spec:
        feed = {ins[0].name: np.stack([frame.real, frame.imag], axis=-1)[None, :, None, :].astype(np.float32)}
        feed.update(caches)
        outs = sess.run(out_names, feed)
        enh = outs[0].squeeze()  # (257, 2)
        enhanced.append(enh[:, 0] + 1j * enh[:, 1])
        for name, value in zip(out_names[1:], outs[1:]):
            caches[[i.name for i in ins[1:]][out_names[1:].index(name)]] = value
    frames_td = np.fft.irfft(np.stack(enhanced), n=N_FFT).astype(np.float32)
    rec = overlap_add(frames_td)[:len(audio)]
    sf.write(wav_out, rec, 16000, subtype="PCM_16")
