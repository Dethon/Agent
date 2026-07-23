from stt_eval.phrases import PHRASES, phrase_for_take


def test_five_phrases():
    assert len(PHRASES) == 5
    assert PHRASES[3] == "Pon un temporizador de diez minutos para la pasta que está al fuego."


def test_mapping_matches_bash_formula():
    # bash: cond=(i-1)%5 ; idx=(cond + (i-1)/5) % 5   (integer division)
    assert phrase_for_take(1) == PHRASES[0]
    assert phrase_for_take(5) == PHRASES[4]
    assert phrase_for_take(6) == PHRASES[1]   # second pass shifts by one
    assert phrase_for_take(10) == PHRASES[0]
    assert phrase_for_take(11) == PHRASES[2]  # third pass shifts by two
