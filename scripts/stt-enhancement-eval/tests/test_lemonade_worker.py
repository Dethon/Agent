import math

from stt_eval import lemonade_worker
from stt_eval.lemonade_worker import _score


class _FakeResponse:
    def read(self):
        return b'{"text": "hola", "segments": []}'

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _capture_post(monkeypatch, tmp_path, **kwargs):
    captured = {}

    def fake_urlopen(req, timeout=None):
        captured["body"] = req.data
        return _FakeResponse()

    monkeypatch.setattr(lemonade_worker.urllib.request, "urlopen", fake_urlopen)
    wav = tmp_path / "clip.wav"
    wav.write_bytes(b"RIFF____WAVEfmt ")
    lemonade_worker._post_transcription("h", 1, "m", str(wav), **kwargs)
    return captured["body"]


def test_post_transcription_includes_prompt_field_when_set(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path, prompt="órdenes breves")

    assert b'name="prompt"' in body
    assert "órdenes breves".encode() in body


def test_post_transcription_omits_prompt_field_when_unset(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path)

    assert b'name="prompt"' not in body
    assert b'name="language"' in body


def test_post_transcription_omits_prompt_field_when_empty(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path, prompt="")

    assert b'name="prompt"' not in body


def test_score_is_exp_of_mean_segment_avg_logprob():
    payload = {"segments": [{"avg_logprob": -0.2}, {"avg_logprob": -0.4}]}
    assert _score(payload) == math.exp((-0.2 + -0.4) / 2)


def test_score_none_when_no_segments():
    assert _score({"text": "hola"}) is None
    assert _score({"segments": []}) is None


def test_score_ignores_segments_without_avg_logprob():
    payload = {"segments": [{"avg_logprob": -0.5}, {"no_speech_prob": 0.1}]}
    assert _score(payload) == math.exp(-0.5)
