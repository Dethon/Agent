"""Staged STT-enhancement eval CLI. Stages: fetch, validate, mix, process, transcribe, report."""
import argparse
from collections.abc import Callable

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


def main() -> None:
    args = build_parser().parse_args()
    args.func(args)


if __name__ == "__main__":
    main()