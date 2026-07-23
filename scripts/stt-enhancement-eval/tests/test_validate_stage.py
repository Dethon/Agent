from stt_eval.validate_stage import clean_wer


def test_exact_match_zero():
    assert clean_wer("¿Qué tiempo va a hacer mañana?", "qué tiempo va a hacer mañana") == 0.0


def test_truncated_take_scores_high():
    ref = "Pon un temporizador de diez minutos para la pasta que está al fuego."
    hyp = "Pon un temporizador de diez"
    assert clean_wer(ref, hyp) > 0.3