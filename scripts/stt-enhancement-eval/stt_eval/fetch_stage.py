"""Idempotent downloads: enrollment voices (scp), SLR73 Spanish speech, MUSAN music, GTCRN onnx."""
import subprocess
from pathlib import Path

PI_DEFAULT = "dethon@192.168.5.45:/home/dethon/jackbot/docker-compose/volumes/voices"
SLR73_URLS = [
    "https://www.openslr.org/resources/73/es_pe_female.zip",
    "https://www.openslr.org/resources/73/es_pe_male.zip",
]
MUSAN_URL = "https://www.openslr.org/resources/17/musan.tar.gz"
GTCRN_URL = "https://github.com/Xiaobin-Rong/gtcrn/raw/main/stream/onnx_models/gtcrn_simple.onnx"


def _have(path: Path) -> bool:
    return path.exists() and any(path.iterdir()) if path.is_dir() else path.exists()


def _sh(cmd: str) -> None:
    print(f"+ {cmd}")
    subprocess.run(cmd, shell=True, check=True)


def run_fetch(data_dir: Path, pi_source: str) -> None:
    voices = data_dir / "voices"
    if not _have(voices):
        voices.mkdir(parents=True, exist_ok=True)
        _sh(f"scp -r '{pi_source}/'* '{voices}/'")
    for spk in sorted(voices.iterdir()):
        if spk.is_dir():
            print(f"voices/{spk.name}: {len(list(spk.glob('enroll-*.wav')))} takes")

    speech = data_dir / "interference/speech"
    if not _have(speech):
        speech.mkdir(parents=True, exist_ok=True)
        for url in SLR73_URLS:
            zip_path = speech / url.rsplit("/", 1)[1]
            _sh(f"curl -fL --retry 3 -o '{zip_path}' '{url}'")
            _sh(f"unzip -q -o '{zip_path}' -d '{speech}' && rm '{zip_path}'")
        # If a URL 404s, check https://www.openslr.org/73/ for current filenames.

    music = data_dir / "interference/music"
    if not _have(music):
        music.mkdir(parents=True, exist_ok=True)
        # Stream-extract only musan/music from the 10 GB tarball; nothing else touches disk.
        _sh(f"curl -fL '{MUSAN_URL}' | tar -xz -C '{music}' --strip-components=1 musan/music")

    model = data_dir / "models/gtcrn_simple.onnx"
    if not model.exists():
        model.parent.mkdir(parents=True, exist_ok=True)
        _sh(f"curl -fL --retry 3 -o '{model}' '{GTCRN_URL}'")
    print("fetch complete")
