import json
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass
class Utterance:
    id: str
    speaker: str
    take: int
    wav: str
    reference: str
    interference: str
    snr_db: float | None


def write_manifest(path: Path, rows: list[Utterance]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(asdict(r), ensure_ascii=False) + "\n")


def read_manifest(path: Path) -> list[Utterance]:
    with path.open(encoding="utf-8") as f:
        return [Utterance(**json.loads(line)) for line in f if line.strip()]
