from stt_eval.textnorm import normalize


def test_strips_punctuation_keeps_diacritics():
    assert normalize("¿Qué tiempo va a hacer mañana?") == "qué tiempo va a hacer mañana"


def test_casefold_and_whitespace():
    assert normalize("  Ok   NABU,  pon música. ") == "ok nabu pon música"


def test_ellipsis_and_dashes():
    assert normalize("Dime — las noticias… ya") == "dime las noticias ya"