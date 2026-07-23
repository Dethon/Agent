import json
import math
import subprocess
import tempfile
from pathlib import Path

from tqdm import tqdm


def _done_wavs(out_jsonl: Path) -> set[str]:
    if not out_jsonl.exists():
        return set()
    with out_jsonl.open(encoding="utf-8") as f:
        return {json.loads(line)["wav"] for line in f if line.strip()}


def _medium(wavs: list[Path], out_jsonl: Path) -> None:
    from faster_whisper import WhisperModel
    # device="auto" fails on this box: CUDA is visible (nvidia-smi works) but cuDNN/cuBLAS
    # runtime libs aren't installed, so ctranslate2's CUDA path errors on first encode
    # (RuntimeError: Library libcublas.so.12 is not found or cannot be loaded). Forced to
    # CPU per the task's sanctioned deviation.
    model = WhisperModel("medium", device="cpu", compute_type="int8")
    with out_jsonl.open("a", encoding="utf-8") as f:
        for wav in tqdm(wavs, desc=f"medium->{out_jsonl.stem}"):
            segments, _ = model.transcribe(str(wav), language="es", beam_size=5)
            segs = list(segments)
            text = " ".join(s.text.strip() for s in segs)
            score = math.exp(sum(s.avg_logprob for s in segs) / len(segs)) if segs else None
            f.write(json.dumps({"wav": str(wav), "text": text, "score": score}, ensure_ascii=False) + "\n")
            # Flush per row: a ~165-clip CPU run takes minutes, and buffered writes make a
            # live run look stalled on disk and lose all progress if the process is killed.
            f.flush()


def _jackbot_network() -> str:
    out = subprocess.run(["docker", "network", "ls", "--format", "{{.Name}}"],
                         capture_output=True, text=True, check=True).stdout.split()
    nets = [n for n in out if "jackbot" in n]
    if not nets:
        raise SystemExit("no jackbot docker network - is the compose stack up?")
    return nets[0]


def _lemonade(wavs: list[Path], out_jsonl: Path) -> None:
    # Prod-parity backend: transcribes through the same Lemonade OpenAI endpoint the hub uses.
    # All wavs must live under one root (the run dir) so a single -v mount covers them.
    root = _mount_root(wavs)
    # Preserve the exact path form the caller passed in (relative/absolute), matching
    # what _medium writes, keyed by basename since the manifest only round-trips names.
    by_name = {w.name: str(w) for w in wavs}
    with tempfile.TemporaryDirectory(dir=root) as td:
        tdp = Path(td)
        with (tdp / "in.jsonl").open("w", encoding="utf-8") as f:
            for wav in wavs:
                f.write(json.dumps({"wav": f"/work/{wav.resolve().relative_to(root.resolve())}"}) + "\n")
        worker = Path(__file__).resolve().parent
        result = subprocess.run([
            "docker", "run", "--rm", "--network", _jackbot_network(),
            "-v", f"{root.resolve()}:/work", "-v", f"{worker}:/s:ro",
            "python:3.12-slim", "python", "/s/lemonade_worker.py",
            "--manifest", f"/work/{tdp.name}/in.jsonl", "--out", f"/work/{tdp.name}/out.jsonl",
        ])
        # Merge whatever the worker managed to write BEFORE checking the return code, so a
        # mid-batch docker failure doesn't discard progress that already reached out.jsonl
        # (which lives inside this TemporaryDirectory and is gone once the `with` exits).
        _merge_worker_output(tdp / "out.jsonl", out_jsonl, by_name)
        if result.returncode != 0:
            raise SystemExit(f"lemonade worker exited {result.returncode}")


def _merge_worker_output(worker_out: Path, out_jsonl: Path, by_name: dict[str, str]) -> None:
    if not worker_out.exists():
        return
    with worker_out.open(encoding="utf-8") as fin, out_jsonl.open("a", encoding="utf-8") as fout:
        for line in fin:
            if not line.strip():
                continue
            row = json.loads(line)
            row["wav"] = by_name[Path(row["wav"]).name]
            fout.write(json.dumps(row, ensure_ascii=False) + "\n")


def _mount_root(wavs: list[Path]) -> Path:
    import os
    common = Path(os.path.commonpath([w.resolve() for w in wavs]))
    # commonpath of a single path returns that path itself; if it isn't a directory
    # (the normal case when exactly one wav remains, e.g. on resume), mount its parent.
    return common if common.is_dir() else common.parent


def transcribe_files(backend: str, wavs: list[Path], out_jsonl: Path) -> None:
    out_jsonl.parent.mkdir(parents=True, exist_ok=True)
    done = _done_wavs(out_jsonl)
    todo = [w for w in wavs if str(w) not in done and str(w.resolve()) not in done]
    if not todo:
        print(f"{out_jsonl}: complete ({len(done)} rows)")
        return
    {"medium": _medium, "lemonade": _lemonade}[backend](todo, out_jsonl)