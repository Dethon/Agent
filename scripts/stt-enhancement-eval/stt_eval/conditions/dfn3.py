"""DeepFilterNet3 via the deepfilternet pip package (48 kHz round-trip)."""
from pathlib import Path

import soundfile as sf
import soxr

_MODEL = None


def _get_model():
    global _MODEL
    if _MODEL is None:
        from df.enhance import init_df
        _MODEL = init_df()  # (model, df_state, _)
    return _MODEL


def process(model_dir: Path, wav_in: Path, wav_out: Path, _voices_dir: Path | None = None) -> None:
    from df.enhance import enhance, load_audio
    model, df_state, _ = _get_model()
    audio, _meta = load_audio(str(wav_in), sr=df_state.sr())
    enhanced = enhance(model, df_state, audio).squeeze().numpy()
    out16 = soxr.resample(enhanced, df_state.sr(), 16000)
    sf.write(wav_out, out16, 16000, subtype="PCM_16")
