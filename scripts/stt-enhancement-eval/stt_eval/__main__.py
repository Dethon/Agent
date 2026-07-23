"""Staged STT-enhancement eval CLI. Stages: fetch, validate, mix, process, transcribe, report."""
import argparse
from collections.abc import Callable
from pathlib import Path

STAGES: dict[str, Callable[[argparse.Namespace], None]] = {}


def _todo(args: argparse.Namespace) -> None:
    raise SystemExit(f"stage not implemented yet")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="stt_eval", description=__doc__)
    sub = parser.add_subparsers(dest="stage", required=True, metavar="{fetch,validate,mix,process,transcribe,report}")
    for name in ("fetch", "validate", "mix", "process", "transcribe", "report"):
        p = sub.add_parser(name)
        p.set_defaults(func=STAGES.get(name, _todo))
        _add_stage_args(name, p)
    return parser


def _add_stage_args(name: str, p: argparse.ArgumentParser) -> None:
    p.add_argument("--run", default="round1", help="run name under runs/")
    p.add_argument("--data", default="data", help="downloads cache dir")
    p.add_argument("--voices", default="data/voices", help="enrollment takes dir")
    if name == "mix":
        p.add_argument("--seed", type=int, default=7)
        p.add_argument("--force", action="store_true",
                       help="re-mix an existing run, dropping its corpus/processed/transcripts")
    if name == "fetch":
        p.add_argument("--pi", default="dethon@192.168.5.45:/home/dethon/jackbot/docker-compose/volumes/voices")
    if name == "transcribe":
        p.add_argument("--backend", choices=["medium", "lemonade"], required=True)
        p.add_argument("--conditions", default="raw", help="comma-list of condition dirs, or 'raw' for the corpus")
    if name == "process":
        p.add_argument("--conditions", default="gtcrn,dfn3")


def _fetch(args: argparse.Namespace) -> None:
    from .fetch_stage import run_fetch
    run_fetch(Path(args.data), args.pi)


STAGES["fetch"] = _fetch


def _validate(args: argparse.Namespace) -> None:
    from .validate_stage import run_validate
    run_validate(Path(args.voices), Path("runs") / args.run)


STAGES["validate"] = _validate


def _mix(args: argparse.Namespace) -> None:
    from .mix_stage import run_mix
    run_dir = Path("runs") / args.run
    takes = run_dir / "takes.jsonl"
    run_mix(Path(args.voices), Path(args.data), run_dir, args.seed,
            takes if takes.exists() else None, force=args.force)


STAGES["mix"] = _mix


def _process(args: argparse.Namespace) -> None:
    from tqdm import tqdm
    from .conditions import PROCESSORS
    from .manifest import read_manifest
    run_dir = Path("runs") / args.run
    rows = read_manifest(run_dir / "manifest.jsonl")
    for cond in args.conditions.split(","):
        proc = PROCESSORS[cond]
        out_dir = run_dir / "processed" / cond
        out_dir.mkdir(parents=True, exist_ok=True)
        for row in tqdm(rows, desc=cond):
            out = out_dir / f"{row.id}.wav"
            if not out.exists():
                proc(Path(args.data) / "models", run_dir / row.wav, out, Path(args.voices))


STAGES["process"] = _process


def _transcribe(args: argparse.Namespace) -> None:
    from .backends import transcribe_files
    run_dir = Path("runs") / args.run
    for cond in (c.strip() for c in args.conditions.split(",")):
        wav_dir = run_dir / "corpus" if cond == "raw" else run_dir / "processed" / cond
        wavs = sorted(wav_dir.glob("*.wav"))
        if not wavs:
            raise SystemExit(f"no wavs in {wav_dir}")
        transcribe_files(args.backend, wavs, run_dir / "transcripts" / args.backend / f"{cond}.jsonl")


STAGES["transcribe"] = _transcribe


def _report(args: argparse.Namespace) -> None:
    from .report_stage import run_report
    run_report(Path("runs") / args.run)


STAGES["report"] = _report


def main() -> None:
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()