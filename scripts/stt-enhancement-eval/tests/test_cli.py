import subprocess
import sys

from stt_eval.__main__ import build_parser


def test_transcribe_stage_accepts_prompt_and_label():
    args = build_parser().parse_args(
        ["transcribe", "--backend", "lemonade", "--prompt", "hola", "--label", "lemonade-prompted"])

    assert args.prompt == "hola"
    assert args.label == "lemonade-prompted"


def test_transcribe_stage_label_defaults_to_none():
    args = build_parser().parse_args(["transcribe", "--backend", "lemonade"])

    assert args.label is None
    assert args.prompt is None


def test_cli_lists_stages():
    out = subprocess.run(
        [sys.executable, "-m", "stt_eval", "--help"],
        capture_output=True, text=True,
    )
    assert out.returncode == 0
    assert "fetch" in out.stdout