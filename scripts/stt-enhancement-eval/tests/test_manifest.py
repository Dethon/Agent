from pathlib import Path
from stt_eval.manifest import Utterance, read_manifest, write_manifest


def test_roundtrip(tmp_path: Path):
    rows = [
        Utterance(id="fran-t1-none-clean", speaker="fran", take=1, wav="corpus/a.wav",
                  reference="hola", interference="none", snr_db=None),
        Utterance(id="fran-t1-speech-snr-05", speaker="fran", take=1, wav="corpus/b.wav",
                  reference="hola", interference="speech", snr_db=-5.0),
    ]
    p = tmp_path / "manifest.jsonl"
    write_manifest(p, rows)
    assert read_manifest(p) == rows
    assert len(p.read_text().strip().splitlines()) == 2
