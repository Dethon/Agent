from stt_eval.manifest import Utterance
from stt_eval.report_stage import decision, score_rows


def _u(uid, interference, snr):
    return Utterance(id=uid, speaker="s", take=1, wav=f"corpus/{uid}.wav",
                     reference="pon un temporizador de diez minutos", interference=interference, snr_db=snr)


def test_score_rows_computes_wer_and_keeps_score():
    man = [_u("a", "speech", 0.0)]
    rows = score_rows(man, {"a.wav": ("pon un temporizador de veinte minutos", 0.62)})
    assert abs(rows[0]["wer"] - 1 / 6) < 1e-9
    assert rows[0]["score"] == 0.62


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
