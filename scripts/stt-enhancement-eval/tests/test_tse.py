"""Pure unit tests for the tse condition's no-leakage logic (no torch, no subprocess).

Covers the two bits of application logic that must stay correct for the enrollment
no-leakage guarantee: filename parsing (`_ID_RE`) and take-exclusion concatenation
(`_enrollment`). The heavy WeSep extraction path is deliberately untouched here.
"""
import numpy as np
import soundfile as sf

from stt_eval.conditions import tse


# --- _ID_RE: parse "<speaker>-t<take>-..." -----------------------------------

def test_id_re_parses_speaker_and_take():
    m = tse._ID_RE.match("Dethon-t1-speech-snr+00.wav")
    assert m and m["speaker"] == "Dethon" and int(m["take"]) == 1


def test_id_re_two_digit_take():
    m = tse._ID_RE.match("Dethon-t10-speech-snr-05.wav")
    assert m and m["speaker"] == "Dethon" and int(m["take"]) == 10


def test_id_re_other_speaker_and_clean_variant():
    m = tse._ID_RE.match("Tradaly-t3-none-clean.wav")
    assert m and m["speaker"] == "Tradaly" and int(m["take"]) == 3


def test_id_re_greedy_absorbs_ambiguous_speaker():
    # Pinned behavior: the `.+` speaker group is GREEDY, so when a name itself
    # contains a "-t<digits>-" segment the LAST such segment is treated as the
    # take and everything before it is the speaker. Our real ids (Dethon/Tradaly)
    # never trigger this, but the behavior is pinned to guard against regressions.
    m = tse._ID_RE.match("weird-t2-name-t5-music-snr+10.wav")
    assert m and m["speaker"] == "weird-t2-name" and int(m["take"]) == 5


def test_id_re_no_take_returns_none():
    assert tse._ID_RE.match("Dethon-speech-clean.wav") is None


# --- _enrollment: concatenate all takes EXCEPT the excluded one --------------

def _write_take(voices_dir, speaker, take, value, n=100):
    d = voices_dir / speaker
    d.mkdir(parents=True, exist_ok=True)
    sf.write(d / f"enroll-{take}.wav", np.full(n, value, dtype="float32"), 16000, subtype="PCM_16")


def test_enrollment_excludes_named_take_and_concatenates_rest(tmp_path):
    # takes 1/2/3 carry distinct DC values so we can detect presence/absence
    _write_take(tmp_path, "Spk", 1, 0.1)
    _write_take(tmp_path, "Spk", 2, 0.2)
    _write_take(tmp_path, "Spk", 3, 0.3)

    out = tse._enrollment(str(tmp_path), "Spk", 2)

    # excluded take (0.2) is absent; the other two are present
    assert not np.any(np.isclose(out, 0.2, atol=2e-3)), "leakage: excluded take present"
    assert np.any(np.isclose(out, 0.1, atol=2e-3))
    assert np.any(np.isclose(out, 0.3, atol=2e-3))
    # length == sum of the two included takes, concatenated in sorted (1, then 3) order
    assert len(out) == 200
    assert np.allclose(out[:100], 0.1, atol=2e-3)
    assert np.allclose(out[100:], 0.3, atol=2e-3)


def test_enrollment_excludes_two_digit_take(tmp_path):
    # split("-")[1] must read "10" as 10, not confuse it with take 1
    _write_take(tmp_path, "Spk", 1, 0.1)
    _write_take(tmp_path, "Spk", 10, 0.5)

    out = tse._enrollment(str(tmp_path), "Spk", 10)

    assert not np.any(np.isclose(out, 0.5, atol=2e-3)), "leakage: two-digit take not excluded"
    assert np.allclose(out, 0.1, atol=2e-3)
    assert len(out) == 100


def test_process_reads_enrollment_from_explicit_voices_dir(tmp_path, monkeypatch):
    # Phase 2 swaps the corpus in via --voices; tse must honor that dir rather than
    # silently deriving data/voices from --data and enrolling against stale audio.
    voices = tmp_path / "field-voices"
    _write_take(voices, "Spk", 1, 0.1)
    _write_take(voices, "Spk", 2, 0.2)
    wav_in = tmp_path / "Spk-t2-speech-snr+00.wav"
    sf.write(wav_in, np.zeros(50, dtype="float32"), 16000, subtype="PCM_16")
    captured = {}

    def fake_extract(model_dir, mixture, enroll):
        captured["enroll"] = enroll
        return mixture

    monkeypatch.setattr(tse, "_extract", fake_extract)
    tse.process(tmp_path / "models", wav_in, tmp_path / "out.wav", voices)

    assert len(captured["enroll"]) == 100  # take 1 only; the mixture's own t2 is excluded
    assert np.allclose(captured["enroll"], 0.1, atol=2e-3)


def test_enrollment_asserts_when_only_excluded_take_exists(tmp_path):
    _write_take(tmp_path, "Solo", 2, 0.2)
    try:
        tse._enrollment(str(tmp_path), "Solo", 2)
    except AssertionError:
        return
    raise AssertionError("expected AssertionError when no takes remain after exclusion")
