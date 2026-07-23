import math

from stt_eval.lemonade_worker import _score


def test_score_is_exp_of_mean_segment_avg_logprob():
    payload = {"segments": [{"avg_logprob": -0.2}, {"avg_logprob": -0.4}]}
    assert _score(payload) == math.exp((-0.2 + -0.4) / 2)


def test_score_none_when_no_segments():
    assert _score({"text": "hola"}) is None
    assert _score({"segments": []}) is None


def test_score_ignores_segments_without_avg_logprob():
    payload = {"segments": [{"avg_logprob": -0.5}, {"no_speech_prob": 0.1}]}
    assert _score(payload) == math.exp(-0.5)
