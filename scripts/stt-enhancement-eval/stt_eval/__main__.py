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
    if name == "fetch":
        p.add_argument("--pi", default="dethon@192.168.5.45:/home/dethon/jackbot/docker-compose/volumes/voices")


def _fetch(args: argparse.Namespace) -> None:
    from .fetch_stage import run_fetch
    run_fetch(Path(args.data), args.pi)


STAGES["fetch"] = _fetch


def _mix(args: argparse.Namespace) -> None:
    from .mix_stage import run_mix
    run_dir = Path("runs") / args.run
    takes = run_dir / "takes.jsonl"
    run_mix(Path(args.voices), Path(args.data), run_dir, args.seed,
            takes if takes.exists() else None)


STAGES["mix"] = _mix


def main() -> None:
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()