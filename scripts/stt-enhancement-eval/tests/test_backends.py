import json
from pathlib import Path

from stt_eval.backends import _merge_worker_output, _mount_root


def test_mount_root_single_file_returns_parent_dir(tmp_path: Path):
    wav = tmp_path / "clip.wav"
    wav.touch()
    assert _mount_root([wav]) == tmp_path


def test_mount_root_two_files_same_dir_returns_that_dir(tmp_path: Path):
    a = tmp_path / "a.wav"
    b = tmp_path / "b.wav"
    a.touch()
    b.touch()
    assert _mount_root([a, b]) == tmp_path


def test_mount_root_sibling_dirs_returns_common_ancestor(tmp_path: Path):
    cond1 = tmp_path / "cond1"
    cond2 = tmp_path / "cond2"
    cond1.mkdir()
    cond2.mkdir()
    a = cond1 / "a.wav"
    b = cond2 / "b.wav"
    a.touch()
    b.touch()
    assert _mount_root([a, b]) == tmp_path


def test_merge_worker_output_rewrites_wav_to_caller_form(tmp_path: Path):
    worker_out = tmp_path / "out.jsonl"
    worker_out.write_text(
        json.dumps({"wav": "/work/clip.wav", "text": "hola", "score": 0.9}) + "\n",
        encoding="utf-8",
    )
    durable = tmp_path / "durable.jsonl"
    by_name = {"clip.wav": "runs/round1/corpus/clip.wav"}

    _merge_worker_output(worker_out, durable, by_name)

    rows = [json.loads(line) for line in durable.read_text(encoding="utf-8").splitlines()]
    assert rows == [{"wav": "runs/round1/corpus/clip.wav", "text": "hola", "score": 0.9}]


def test_merge_worker_output_appends_to_existing_durable_file(tmp_path: Path):
    worker_out = tmp_path / "out.jsonl"
    worker_out.write_text(
        json.dumps({"wav": "/work/b.wav", "text": "b", "score": 0.5}) + "\n",
        encoding="utf-8",
    )
    durable = tmp_path / "durable.jsonl"
    durable.write_text(json.dumps({"wav": "a.wav", "text": "a", "score": 0.1}) + "\n", encoding="utf-8")
    by_name = {"b.wav": "b.wav"}

    _merge_worker_output(worker_out, durable, by_name)

    lines = durable.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 2


def test_merge_worker_output_missing_file_is_a_noop(tmp_path: Path):
    durable = tmp_path / "durable.jsonl"

    _merge_worker_output(tmp_path / "missing-out.jsonl", durable, {})

    assert not durable.exists()
