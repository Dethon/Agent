#!/usr/bin/env python3
"""Round-trip check for the patched wyoming-whisper score emission.

Synthesizes Spanish speech via wyoming-piper, transcribes it via wyoming-whisper, and
asserts the transcript event carries a healthy `score`. Also feeds pure noise and reports
how it fares (VAD usually strips it to an empty transcript, which is the ideal outcome).

The wyoming ports are not published to the host; run inside the compose network:

    NET=$(docker network ls --format '{{.Name}}' | grep jackbot)
    docker run --rm --network "$NET" -v "$PWD/scripts:/s:ro" python:3.12-slim \
        python /s/verify-whisper-score.py
"""
import json
import random
import socket
import sys

WHISPER = ("wyoming-whisper", 10300)
PIPER = ("wyoming-piper", 10200)
PHRASE = "Enciende la luz del salón, por favor."


def write_event(sock, etype, data=None, payload=b""):
    data_bytes = json.dumps(data or {}).encode()
    header = {
        "type": etype,
        "version": "1.0.0",
        "data_length": len(data_bytes),
        "payload_length": len(payload),
    }
    sock.sendall(json.dumps(header).encode() + b"\n" + data_bytes + payload)


def read_event(f):
    line = f.readline()
    if not line:
        return None
    header = json.loads(line)
    data = header.get("data") or {}
    if header.get("data_length"):
        data = json.loads(f.read(header["data_length"]))
    payload = f.read(header["payload_length"]) if header.get("payload_length") else b""
    return header["type"], data, payload


def synthesize(text):
    with socket.create_connection(PIPER, timeout=120) as s:
        f = s.makefile("rb")
        write_event(s, "synthesize", {"text": text})
        rate, chunks = 22050, []
        while True:
            evt = read_event(f)
            if evt is None:
                break
            etype, data, payload = evt
            if etype == "audio-start":
                rate = data.get("rate", 22050)
            elif etype == "audio-chunk":
                chunks.append(payload)
            elif etype == "audio-stop":
                break
        return rate, b"".join(chunks)


def resample_to_16k(pcm, rate):
    if rate == 16000:
        return pcm
    samples = [int.from_bytes(pcm[i:i + 2], "little", signed=True) for i in range(0, len(pcm) - 1, 2)]
    n_out = int(len(samples) * 16000 / rate)
    out = bytearray()
    for i in range(n_out):
        pos = i * (len(samples) - 1) / max(n_out - 1, 1)
        lo = int(pos)
        hi = min(lo + 1, len(samples) - 1)
        frac = pos - lo
        val = int(samples[lo] * (1 - frac) + samples[hi] * frac)
        out += max(min(val, 32767), -32768).to_bytes(2, "little", signed=True)
    return bytes(out)


def transcribe(pcm):
    fmt = {"rate": 16000, "width": 2, "channels": 1, "timestamp": 0}
    with socket.create_connection(WHISPER, timeout=300) as s:
        f = s.makefile("rb")
        write_event(s, "transcribe", {"language": "es"})
        write_event(s, "audio-start", fmt)
        for i in range(0, len(pcm), 3200):
            write_event(s, "audio-chunk", fmt, pcm[i:i + 3200])
        write_event(s, "audio-stop", {"timestamp": 0})
        while True:
            evt = read_event(f)
            if evt is None:
                return None
            etype, data, _ = evt
            if etype == "transcript":
                return data


def noise(seconds=2.0):
    rnd = random.Random(42)
    return b"".join(
        max(min(int(rnd.gauss(0, 2000)), 32767), -32768).to_bytes(2, "little", signed=True)
        for _ in range(int(16000 * seconds))
    )


def main():
    rate, speech = synthesize(PHRASE)
    if not speech:
        print("FAIL: piper returned no audio")
        sys.exit(1)
    speech_result = transcribe(resample_to_16k(speech, rate))
    print("speech:", json.dumps(speech_result, ensure_ascii=False))
    noise_result = transcribe(noise())
    print("noise: ", json.dumps(noise_result, ensure_ascii=False))

    ok = True
    if not speech_result or not speech_result.get("text", "").strip():
        print("FAIL: speech produced no transcript")
        ok = False
    elif "score" not in speech_result:
        print("FAIL: no score on speech transcript - patch not active?")
        ok = False
    elif speech_result["score"] < 0.5:
        print(f"FAIL: speech score suspiciously low ({speech_result['score']:.3f})")
        ok = False

    if noise_result and noise_result.get("text", "").strip():
        if "score" not in noise_result:
            print("FAIL: noise transcript missing score")
            ok = False
        elif noise_result["score"] >= 0.4:
            print(f"WARN: noise scored {noise_result['score']:.3f} (>= gate 0.4) - would pass")

    print("PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
