"""Reference phrases and take->phrase mapping, mirroring scripts/enroll-voice.sh verbatim."""

PHRASES = [
    "Ok nabu, pon música tranquila en el salón y baja un poco el volumen, por favor.",
    "¿Qué tiempo va a hacer mañana por la tarde aquí en casa?",
    "Recuérdame sacar la basura esta noche antes de irme a dormir.",
    "Pon un temporizador de diez minutos para la pasta que está al fuego.",
    "Dime las noticias de hoy y cómo está el tráfico para ir al centro.",
]

_N_CONDITIONS = 5


def phrase_for_take(take_index: int) -> str:
    cond = (take_index - 1) % _N_CONDITIONS
    idx = (cond + (take_index - 1) // _N_CONDITIONS) % len(PHRASES)
    return PHRASES[idx]
