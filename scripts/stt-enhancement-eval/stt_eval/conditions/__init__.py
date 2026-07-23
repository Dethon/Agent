from collections.abc import Callable
from pathlib import Path

from . import dfn3, gtcrn, tse

# (model_dir, wav_in, wav_out, voices_dir) -- voices_dir is the CLI's --voices, so
# corpus location stays an input for every condition (only tse consumes it today).
PROCESSORS: dict[str, Callable[[Path, Path, Path, Path], None]] = {
    "gtcrn": gtcrn.process,
    "dfn3": dfn3.process,
    "tse": tse.process,
}
