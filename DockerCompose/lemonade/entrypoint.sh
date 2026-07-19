#!/bin/sh
# Maps the single STT_BACKEND env var (cpu|gpu, default gpu) onto Lemonade's whisper.cpp
# device selection (config.json — the same mechanism Lemonade's docker docs use for
# llamacpp) and pre-pulls the model. Both tiers run the same model, so the hub needs no
# corresponding setting; STT_BACKEND is container-side only.
set -eu

BACKEND="${STT_BACKEND:-gpu}"
# Pre-pull target only. Keep in sync with the hub's Stt__OpenAi__Model if you override it;
# a mismatch just means the wrong model is warmed and the right one downloads lazily.
MODEL="${STT_MODEL:-Whisper-Medium}"

case "$BACKEND" in
  cpu)  WHISPER_BACKEND="cpu" ;;
  gpu)  WHISPER_BACKEND="vulkan" ;;
  npu)  echo "STT_BACKEND selects the whisper.cpp device, whose NPU option is Windows-only. The Linux NPU tier goes through Lemonade's separate 'flm' recipe instead: leave STT_BACKEND on cpu or gpu and apply docker-compose.override.npu.yml with STT_MODEL set to an flm-recipe ASR model." >&2
        exit 1 ;;
  *)    echo "Unknown STT_BACKEND '$BACKEND' (expected cpu|gpu)" >&2; exit 1 ;;
esac

CONFIG_DIR="${LEMONADE_CONFIG_DIR:-$HOME/.cache/lemonade}"
mkdir -p "$CONFIG_DIR"
# Dedicated STT/TTS container: whispercpp is the only recipe we configure, so a plain
# overwrite is fine (no llamacpp settings to preserve).
cat > "$CONFIG_DIR/config.json" <<EOF
{
  "whispercpp": { "backend": "$WHISPER_BACKEND" }
}
EOF

echo "lemonade: whispercpp.backend=$WHISPER_BACKEND model=$MODEL"

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