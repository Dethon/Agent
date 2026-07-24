#!/bin/sh
# Maps the single STT_BACKEND env var (cpu|gpu, default gpu) onto Lemonade's whisper.cpp
# device selection (config.json — the same mechanism Lemonade's docker docs use for
# llamacpp), restores the Wyoming-era decode-quality flags via whispercpp.args (appended
# verbatim to the spawned whisper-server command line), and pre-pulls the model. Both
# tiers run the same model, so the hub needs no corresponding setting; everything here is
# container-side only. The NPU/flm tier ignores whispercpp.* entirely.
set -eu

BACKEND="${STT_BACKEND:-gpu}"
# Pre-pull target only. Keep in sync with the hub's Stt__OpenAi__Model if you override it;
# a mismatch just means the wrong model is warmed and the right one downloads lazily.
MODEL="${STT_MODEL:-Whisper-Medium}"

# ${VAR-default}: unset inherits the tuned default, set-but-empty disables that flag.
# whisper-server's own beam default is -1 (greedy); 5 matches the old wyoming-whisper.
# The initial prompt biases spelling/vocabulary toward Castilian assistant turns; it must
# not contain double quotes (it is embedded in config.json as a quoted argument).
BEAM_SIZE="${STT_BEAM_SIZE-5}"
VAD_THRESHOLD="${STT_VAD_THRESHOLD-0.6}"
INITIAL_PROMPT="${STT_INITIAL_PROMPT-Asistente de voz en español de España (castellano): domótica, recordatorios, listas de la compra, el tiempo y preguntas generales. Nombres propios y ciudades españolas, p. ej. Valladolid.}"

case "$BACKEND" in
  cpu)  WHISPER_BACKEND="cpu" ;;
  gpu)  WHISPER_BACKEND="vulkan" ;;
  npu)  echo "STT_BACKEND selects the whisper.cpp device, whose NPU option is Windows-only. The Linux NPU tier goes through Lemonade's separate 'flm' recipe instead: leave STT_BACKEND on cpu or gpu and apply docker-compose.override.npu.yml with STT_MODEL set to an flm-recipe ASR model." >&2
        exit 1 ;;
  *)    echo "Unknown STT_BACKEND '$BACKEND' (expected cpu|gpu)" >&2; exit 1 ;;
esac

CONFIG_DIR="${LEMONADE_CONFIG_DIR:-$HOME/.cache/lemonade}"
mkdir -p "$CONFIG_DIR"

WHISPER_ARGS=""
if [ -n "$BEAM_SIZE" ]; then
  WHISPER_ARGS="--beam-size $BEAM_SIZE"
fi
# The \" survive into config.json as JSON escapes, so the prompt reaches whisper-server as
# one quoted argument (lemond's parse_custom_args honors the quotes).
if [ -n "$INITIAL_PROMPT" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --prompt \\\"$INITIAL_PROMPT\\\""
fi

# Silero VAD trims non-speech before the decoder (fewer silence/noise hallucinations);
# threshold 0.6 rejects borderline noise wakes — the 2026-07 gibberish protection carried
# over from wyoming-whisper, same rollback signal (quiet/far speech getting "ignored").
# whisper.cpp doesn't bundle the model, so fetch it once into the cache volume. Fail-open:
# an unreachable HF means this boot runs without VAD, never a crash loop.
VAD_MODEL="$CONFIG_DIR/vad/ggml-silero-v5.1.2.bin"
if [ -n "$VAD_THRESHOLD" ]; then
  if [ ! -s "$VAD_MODEL" ]; then
    mkdir -p "$CONFIG_DIR/vad"
    curl -fsSL --max-time 120 -o "$VAD_MODEL.tmp" \
      "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" \
      && mv "$VAD_MODEL.tmp" "$VAD_MODEL" \
      || { rm -f "$VAD_MODEL.tmp"; echo "lemonade: VAD model download failed; starting without VAD" >&2; }
  fi
  if [ -s "$VAD_MODEL" ]; then
    WHISPER_ARGS="$WHISPER_ARGS --vad --vad-model $VAD_MODEL --vad-threshold $VAD_THRESHOLD"
  fi
fi
WHISPER_ARGS="${WHISPER_ARGS# }"

# Dedicated STT/TTS container: whispercpp is the only recipe we configure, so a plain
# overwrite is fine (no llamacpp settings to preserve).
cat > "$CONFIG_DIR/config.json" <<EOF
{
  "whispercpp": { "backend": "$WHISPER_BACKEND", "args": "$WHISPER_ARGS" }
}
EOF

echo "lemonade: whispercpp.backend=$WHISPER_BACKEND model=$MODEL args=$WHISPER_ARGS"

# Test seam: config-mapping can be verified without starting the server (no GPU, no model pull).
if [ "${STT_CONFIG_ONLY:-0}" = "1" ]; then
  exit 0
fi

# Pre-pull the tier's whisper model once the server is up so the first utterance doesn't
# pay the download; Kokoro (TTS) downloads on first use. Best-effort by design.
(
  i=0
  while [ "$i" -lt 60 ]; do
    sleep 2
    if curl -fsS "http://127.0.0.1:13305/api/v1/health" >/dev/null 2>&1; then
      curl -fsS -X POST "http://127.0.0.1:13305/api/v1/pull" \
        -H "Content-Type: application/json" \
        -d "{\"model_name\": \"$MODEL\"}" >/dev/null 2>&1 || true
      exit 0
    fi
    i=$((i + 1))
  done
) &

exec ./lemond --host 0.0.0.0 --port 13305