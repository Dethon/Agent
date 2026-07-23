import numpy as np


def active_rms(x: np.ndarray, frame: int = 512) -> float:
    n = len(x) // frame * frame
    frames = x[:n].reshape(-1, frame)
    rms = np.sqrt(np.mean(frames.astype(np.float64) ** 2, axis=1))
    thr = 0.1 * rms.max()
    act = rms[rms >= thr]
    return float(np.sqrt(np.mean(act**2)))


def mix_at_snr(speech: np.ndarray, interference: np.ndarray, snr_db: float) -> np.ndarray:
    s = active_rms(speech)
    i = float(np.sqrt(np.mean(interference.astype(np.float64) ** 2)))
    scale = s / (i * 10 ** (snr_db / 20))
    mixed = speech + interference * scale
    peak = float(np.max(np.abs(mixed)))
    if peak > 0.99:
        mixed = mixed * (0.99 / peak)
    return mixed.astype(np.float32)