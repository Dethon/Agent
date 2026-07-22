from collections.abc import Callable
from pathlib import Path

from . import dfn3, gtcrn

PROCESSORS: dict[str, Callable[[Path, Path, Path], None]] = {
    "gtcrn": gtcrn.process,
    "dfn3": dfn3.process,
}
