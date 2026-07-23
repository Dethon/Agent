"""Gates each enrollment take: reference is trustworthy only if whisper-medium agrees on clean audio."""
import json
from pathlib import Path

import jiwer

from .backends import transcribe_files
from .phrases import phrase_for_take
from .textnorm import normalize


def clean_wer(reference: str, hypothesis: str) -> float:
    return jiwer.wer(normalize(reference), normalize(hypothesis))


def run_validate(voices_dir: Path, run_dir: Path, max_clean_wer: float = 0.3) -> None:
    takes = sorted(voices_dir.glob("*/enroll-*.wav"))
    if not takes:
        raise SystemExit(f"no takes under {voices_dir} - run fetch first")
    out_jsonl = run_dir / "clean_transcripts.jsonl"
    transcribe_files("medium", takes, out_jsonl)
    by_wav = {}
    with out_jsonl.open(encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            by_wav[Path(row["wav"]).name + "|" + Path(row["wav"]).parent.name] = row["text"]
    run_dir.mkdir(parents=True, exist_ok=True)
    n_excluded = 0
    with (run_dir / "takes.jsonl").open("w", encoding="utf-8") as f:
        for wav in takes:
            speaker, take = wav.parent.name, int(wav.stem.split("-")[1])
            ref = phrase_for_take(take)
            hyp = by_wav.get(wav.name + "|" + speaker, "")
            wer = clean_wer(ref, hyp)
            included = wer <= max_clean_wer
            n_excluded += not included
            f.write(json.dumps({"speaker": speaker, "take": take, "wav": str(wav),
                                "reference": ref, "clean_wer": round(wer, 3),
                                "included": included}, ensure_ascii=False) + "\n")
    print(f"takes: {len(takes)}, excluded (clean WER > {max_clean_wer}): {n_excluded}")
