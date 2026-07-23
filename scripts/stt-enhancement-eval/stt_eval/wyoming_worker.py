"""Stdlib-only Wyoming STT worker, run inside the compose network via docker run.

Framing mirrors scripts/verify-whisper-score.py (the patched wyoming-whisper adds
`score` to the transcript event).
"""
import argparse
import json
import socket
import wave


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


def transcribe_wav(host, port, wav_path):
    with wave.open(wav_path, "rb") as w:
        assert (w.getframerate(), w.getsampwidth(), w.getnchannels()) == (16000, 2, 1), wav_path
        pcm = w.readframes(w.getnframes())
    fmt = {"rate": 16000, "width": 2, "channels": 1, "timestamp": 0}
    with socket.create_connection((host, port), timeout=300) as s:
        f = s.makefile("rb")
        write_event(s, "transcribe", {"language": "es"})
        write_event(s, "audio-start", fmt)
        for i in range(0, len(pcm), 3200):
            write_event(s, "audio-chunk", fmt, pcm[i:i + 3200])
        write_event(s, "audio-stop", {"timestamp": 0})
        while True:
            evt = read_event(f)
            if evt is None:
                return {"text": "", "score": None}
            etype, data, _ = evt
            if etype == "transcript":
                return {"text": data.get("text", ""), "score": data.get("score")}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="wyoming-whisper")
    ap.add_argument("--port", type=int, default=10300)
    ap.add_argument("--manifest", required=True, help="jsonl of {'wav': path}")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    with open(args.manifest, encoding="utf-8") as fin, open(args.out, "w", encoding="utf-8") as fout:
        for line in fin:
            wav = json.loads(line)["wav"]
            row = transcribe_wav(args.host, args.port, wav)
            row["wav"] = wav
            fout.write(json.dumps(row, ensure_ascii=False) + "\n")
            # Flush per row: mirrors _medium's per-row flush so a mid-batch docker kill
            # (or a non-zero exit the caller merges around) still leaves completed rows
            # on disk in the worker's out.jsonl instead of buffered and lost.
            fout.flush()
            print(wav, "->", row["text"][:60], flush=True)


if __name__ == "__main__":
    main()