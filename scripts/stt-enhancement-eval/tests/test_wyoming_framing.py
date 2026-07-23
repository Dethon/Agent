import json
import socket
from stt_eval.wyoming_worker import read_event, write_event


def test_event_roundtrip():
    a, b = socket.socketpair()
    payload = b"\x01\x02" * 1600
    write_event(a, "audio-chunk", {"rate": 16000, "width": 2, "channels": 1}, payload)
    a.close()
    etype, data, got = read_event(b.makefile("rb"))
    assert etype == "audio-chunk"
    assert data["rate"] == 16000
    assert got == payload