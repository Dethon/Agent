import json

import pytest

from stt_eval.manifest import Utterance, write_manifest
from stt_eval.report_stage import decision, run_report, score_rows


def _u(uid, interference, snr):
    return Utterance(id=uid, speaker="s", take=1, wav=f"corpus/{uid}.wav",
                     reference="pon un temporizador de diez minutos", interference=interference, snr_db=snr)


def test_score_rows_computes_wer_and_keeps_score():
    man = [_u("a", "speech", 0.0)]
    rows = score_rows(man, {"a.wav": ("pon un temporizador de veinte minutos", 0.62)})
    assert abs(rows[0]["wer"] - 1 / 6) < 1e-9
    assert rows[0]["score"] == 0.62


def _run_dir(tmp_path, transcribed):
    write_manifest(tmp_path / "manifest.jsonl", [_u("a", "speech", 0.0), _u("b", "speech", 0.0)])
    tdir = tmp_path / "transcripts" / "medium"
    tdir.mkdir(parents=True)
    with (tdir / "raw.jsonl").open("w", encoding="utf-8") as f:
        for uid in transcribed:
            f.write(json.dumps({"wav": f"{uid}.wav", "text": "pon un temporizador de diez minutos",
                                "score": 0.5}) + "\n")
    return tmp_path


def test_report_fails_loudly_on_missing_transcripts(tmp_path):
    # A transcribe run interrupted mid-batch must not silently score the missing
    # utterances as empty hypotheses (jiwer: ~100% WER) - that fabricates a FAIL.
    with pytest.raises(SystemExit, match="1/2"):
        run_report(_run_dir(tmp_path, ["a"]))


def test_report_with_full_coverage_writes_report(tmp_path):
    run_dir = _run_dir(tmp_path, ["a", "b"])
    run_report(run_dir)
    assert (run_dir / "report.md").exists()


def test_decision_rule_fails_on_high_snr_regression():
    # The dfn3 shape from round 1: clears the low-SNR bar and leaves clean untouched,
    # but badly regresses the +15 dB cell (19.2% -> 31.2% WER). The spec's gate is
    # "does not regress the clean/HIGH-SNR cells", so this must FAIL.
    raw = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.8},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.192},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    model = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.4},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.312},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    d = decision(raw_cells=raw, model_cells=model)
    assert d["high_snr_wer_delta"] > 0.02
    assert not d["passes"]


def test_decision_rule():
    raw = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.8},
        {"interference": "speech", "snr_db": 5.0, "wer": 0.5},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.1},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    good = [
        {"interference": "speech", "snr_db": 0.0, "wer": 0.4},
        {"interference": "speech", "snr_db": 5.0, "wer": 0.3},
        {"interference": "speech", "snr_db": 15.0, "wer": 0.1},
        {"interference": "none", "snr_db": None, "wer": 0.05},
    ]
    d = decision(raw_cells=raw, model_cells=good)
    assert d["passes"] and d["rel_improvement_low_snr"] > 0.2
    bad_clean = [dict(r) for r in good]
    bad_clean[-1]["wer"] = 0.12  # clean regression
    assert not decision(raw_cells=raw, model_cells=bad_clean)["passes"]
