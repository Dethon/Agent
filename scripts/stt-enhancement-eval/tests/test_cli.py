import subprocess
import sys


def test_cli_lists_stages():
    out = subprocess.run(
        [sys.executable, "-m", "stt_eval", "--help"],
        capture_output=True, text=True,
    )
    assert out.returncode == 0
    assert "fetch" in out.stdout