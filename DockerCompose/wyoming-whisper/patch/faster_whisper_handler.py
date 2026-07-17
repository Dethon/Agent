"""Event handler for clients of the server."""

import logging
import math
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union

import faster_whisper

from .const import Transcriber

_LOGGER = logging.getLogger(__name__)


class FasterWhisperTranscriber(Transcriber):
    """Event handler for clients."""

    def __init__(
        self,
        model_id: str,
        cache_dir: Union[str, Path],
        device: str = "cpu",
        compute_type: str = "default",
        cpu_threads: int = 4,
        vad_parameters: Optional[Dict[str, Any]] = None,
        task: Optional[str] = None,
    ) -> None:
        self.vad_filter = vad_parameters is not None
        self.vad_parameters = vad_parameters
        self.task = task

        self.model = faster_whisper.WhisperModel(
            model_id,
            download_root=str(cache_dir),
            device=device,
            compute_type=compute_type,
            cpu_threads=cpu_threads,
        )

    def transcribe(
        self,
        wav_path: Union[str, Path],
        language: Optional[str],
        beam_size: int = 5,
        initial_prompt: Optional[str] = None,
    ) -> Tuple[str, Optional[Dict[str, float]]]:
        # PATCH(jackbot): return (text, stats) instead of bare text so the dispatch
        # handler can attach transcription-quality stats to the transcript event.
        # dispatch_handler.py is patched in lockstep and tolerates both shapes, so
        # other backends (bare str) keep working unmodified.

        kwargs = {
            "beam_size": beam_size,
            "language": language,
            "initial_prompt": initial_prompt,
            "vad_filter": self.vad_filter,
            "vad_parameters": self.vad_parameters,
        }
        if self.task:
            kwargs["task"] = self.task

        segments, _info = self.model.transcribe(str(wav_path), **kwargs)
        # PATCH(jackbot): materialize the lazy generator so per-segment stats are readable.
        segments = list(segments)
        text = " ".join(segment.text for segment in segments)
        return text, _stats_of(segments)


# PATCH(jackbot): duration-weighted quality stats for one transcription.
# None when VAD/whisper produced zero segments (never divide by zero).
def _stats_of(segments: List[Any]) -> Optional[Dict[str, float]]:
    if not segments:
        return None

    weights = [max(s.end - s.start, 1e-6) for s in segments]
    total = sum(weights)
    avg_logprob = sum(w * s.avg_logprob for w, s in zip(weights, segments)) / total
    no_speech_prob = sum(w * s.no_speech_prob for w, s in zip(weights, segments)) / total
    return {
        # exp(mean token logprob) ~= mean token probability, in (0, 1].
        # The hub gate (VoiceSettings.ConfidenceThreshold = 0.4) sits near whisper's
        # canonical junk threshold: 0.4 ~= exp(-0.92) vs logprob_threshold = -1.0.
        "score": math.exp(min(avg_logprob, 0.0)),
        "avg_logprob": avg_logprob,
        "no_speech_prob": no_speech_prob,
        "compression_ratio": max(s.compression_ratio for s in segments),
    }
