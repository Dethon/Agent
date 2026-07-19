#!/bin/sh
# On-box smoke test for mcp-lemonade (run from DockerCompose/): sh lemonade/smoke.sh [host:port]
# Scriptable slice of the on-box validation checklist: health, verbose_json quality
# signals, and incremental PCM streaming.
# Requires curl and python3 ON THE HOST (python3 only to synthesize the test WAV — swap in
# any 16 kHz mono 16-bit wav at /tmp/lemonade-smoke.wav if python3 is unavailable).
set -eu
HOST="${1:-localhost:13305}"
# Override for the NPU tier: sh lemonade/smoke.sh localhost:13305 whisper-v3-turbo-FLM
MODEL="${2:-${STT_MODEL:-Whisper-Medium}}"

echo "== health =="
curl -fsS "http://$HOST/api/v1/health" && echo

echo "== transcription: 1 s 440 Hz tone; expect JSON with text + segments carrying avg_logprob/no_speech_prob =="
python3 - <<'EOF'
import math, struct, wave
w = wave.open('/tmp/lemonade-smoke.wav', 'wb')
w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
w.writeframes(b''.join(struct.pack('<h', int(8000 * math.sin(2 * math.pi * 440 * i / 16000)))
                       for i in range(16000)))
w.close()
EOF
curl -fsS -X POST "http://$HOST/v1/audio/transcriptions" \
  -F "file=@/tmp/lemonade-smoke.wav" -F "model=$MODEL" \
  -F "response_format=verbose_json" -F "language=es"
echo

echo "== speech: streamed pcm; ttfb well under total proves incremental streaming =="
curl -fsS -N -o /tmp/lemonade-smoke.pcm \
  -w 'ttfb=%{time_starttransfer}s total=%{time_total}s bytes=%{size_download}\n' \
  -X POST "http://$HOST/v1/audio/speech" \
  -H "Content-Type: application/json" \
  -d '{"model":"kokoro-v1","input":"Hola, esto es una prueba de síntesis de voz en streaming.","voice":"ef_dora","speed":1.0,"response_format":"pcm","stream_format":"audio"}'
echo "pcm is 24 kHz mono s16le: 48000 bytes per second of audio"