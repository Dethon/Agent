"""Streaming GTCRN inference (gtcrn_simple.onnx from Xiaobin-Rong/gtcrn stream/)."""
from pathlib import Path
from typing import TYPE_CHECKING, cast

import numpy as np
import soundfile as sf
from numpy.typing import NDArray

if TYPE_CHECKING:
    import onnxruntime as ort

N_FFT, HOP = 512, 256
_WIN = np.sqrt(np.hanning(N_FFT + 1)[:-1]).astype(np.float32)
_session_cache: "ort.InferenceSession | None" = None


def stft_frames(audio: NDArray[np.float32]) -> NDArray[np.complex128]:
    pad = (-len(audio)) % HOP
    audio = np.pad(audio, (N_FFT - HOP, pad))
    n_frames = (len(audio) - N_FFT) // HOP + 1
    frames = np.stack([audio[i * HOP:i * HOP + N_FFT] * _WIN for i in range(n_frames)])
    return np.fft.rfft(frames, axis=1)


def overlap_add(frames: NDArray[np.float32]) -> NDArray[np.float32]:
    out = np.zeros((len(frames) - 1) * HOP + N_FFT, dtype=np.float32)
    for i, frame in enumerate(frames):
        out[i * HOP:i * HOP + N_FFT] += frame * _WIN
    return out[N_FFT - HOP:]


def _session(model_path: Path) -> "ort.InferenceSession":
    global _session_cache
    if _session_cache is None:
        import onnxruntime as ort
        _session_cache = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    return _session_cache


def process(model_dir: Path, wav_in: Path, wav_out: Path, _voices_dir: Path | None = None) -> None:
    sess = _session(model_dir / "gtcrn_simple.onnx")
    ins = sess.get_inputs()
    out_names = [o.name for o in sess.get_outputs()]
    cache_names = [i.name for i in ins[1:]]
    caches = {
        i.name: np.zeros([d if isinstance(d, int) else 1 for d in i.shape], dtype=np.float32)
        for i in ins[1:]
    }
    audio, sr = sf.read(wav_in, dtype="float32")
    audio = np.asarray(audio, dtype=np.float32)  # dtype="float32" already; this only pins the type
    assert sr == 16000, wav_in
    spec = stft_frames(audio)
    enhanced: list[NDArray[np.complex128]] = []
    for frame in spec:
        feed = {ins[0].name: np.stack([frame.real, frame.imag], axis=-1)[None, :, None, :].astype(np.float32)}
        feed.update(caches)
        # Every output of gtcrn_simple.onnx is a dense float32 tensor; ort types run() as the
        # wider OrtValue union (sparse tensors, sequences, maps), which never occurs here.
        outs = cast("list[NDArray[np.float32]]", sess.run(out_names, feed))
        enh = outs[0].squeeze()  # (257, 2)
        enhanced.append(enh[:, 0] + 1j * enh[:, 1])
        # outs[1:] are the next-step caches, in the same order as the cache inputs ins[1:].
        for cache_name, value in zip(cache_names, outs[1:]):
            caches[cache_name] = value
    frames_td = np.fft.irfft(np.stack(enhanced), n=N_FFT).astype(np.float32)
    rec = overlap_add(frames_td)[:len(audio)]
    sf.write(wav_out, rec, 16000, subtype="PCM_16")
