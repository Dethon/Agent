#!/usr/bin/env bash
set -euo pipefail

# Run a wyoming-satellite on this WSL host, routing audio through WSLg PulseAudio.
#
# Topology: wyoming-satellite is itself a Wyoming *server* listening on its --uri
# (SAT_PORT). The McpChannelVoice hub (in Docker) dials OUT to it as a client — see
# the satellite's Address in McpChannelVoice/appsettings.Development.json, which
# points at tcp://host.docker.internal:SAT_PORT. Local wake detection runs here via
# wyoming-openwakeword; once the wake word fires the satellite streams mic audio to
# the connected hub and plays back the TTS audio the hub streams in return.
#
# Defaults match `McpChannelVoice/appsettings.Development.json` — change SAT_ID
# to one of {kitchen-01, living-room-01, bedroom-01} or edit appsettings to add
# a new satellite (and give it an Address so the hub dials it).
#
# Usage:
#   ./scripts/wsl-satellite.sh
#   SAT_ID=bedroom-01 WAKE_WORD=hey_jarvis ./scripts/wsl-satellite.sh

SAT_ID="${SAT_ID:-kitchen-01}"
WAKE_WORD="${WAKE_WORD:-hey_jarvis}"
SAT_PORT="${SAT_PORT:-10800}"
WW_PORT="${WW_PORT:-10400}"
WW_THRESHOLD="${WW_THRESHOLD:-0.5}"
WW_DEBUG="${WW_DEBUG:-0}"

WW_VENV="$HOME/.local/share/pipx/venvs/wyoming-openwakeword"
SAT_VENV="$HOME/.local/share/pipx/venvs/wyoming-satellite"

if [[ ! -x "$WW_VENV/bin/python" ]] || [[ ! -x "$SAT_VENV/bin/python" ]]; then
  echo "wyoming-satellite or wyoming-openwakeword not installed via pipx." >&2
  echo "Install with:" >&2
  echo "  pipx install wyoming-satellite wyoming-openwakeword" >&2
  echo "  # Pin numpy<2 in the openwakeword venv (the bundled tflite_runtime is" >&2
  echo "  # built against numpy 1.x and crashes loading hey_jarvis with numpy 2.x):" >&2
  echo "  $WW_VENV/bin/python -m pip install 'numpy<2'" >&2
  exit 1
fi

if [[ -z "${PULSE_SERVER:-}" ]] && [[ ! -S /mnt/wslg/PulseServer ]]; then
  echo "PulseAudio not detected. Are you running inside WSLg?" >&2
  echo "Try: pactl info" >&2
  exit 1
fi

cleanup() {
  if [[ -n "${WW_PID:-}" ]]; then
    kill "$WW_PID" 2>/dev/null || true
    wait "$WW_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# Wipe any stale instances from a prior run (Ctrl+C of the foreground satellite
# doesn't necessarily clean up the backgrounded openwakeword, so port 10400 may
# still be held). Quiet — only kills our own.
pkill -u "$USER" -f wyoming_openwakeword 2>/dev/null || true
pkill -u "$USER" -f wyoming_satellite 2>/dev/null || true
sleep 0.5

echo "[1/2] wyoming-openwakeword listening on tcp://0.0.0.0:$WW_PORT (model=$WAKE_WORD threshold=$WW_THRESHOLD)"
WW_EXTRA=()
[[ "$WW_DEBUG" == "1" ]] && WW_EXTRA+=(--debug --debug-probability)
"$WW_VENV/bin/python" -m wyoming_openwakeword \
  --uri "tcp://0.0.0.0:$WW_PORT" \
  --preload-model "$WAKE_WORD" \
  --threshold "$WW_THRESHOLD" \
  "${WW_EXTRA[@]}" \
  > /tmp/wyoming-openwakeword.log 2>&1 &
WW_PID=$!

# Wait for openwakeword to bind its port (first run downloads the model — can take >30s)
for _ in $(seq 1 120); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$WW_PORT") 2>/dev/null; then
    exec 3<&- 3>&-
    break
  fi
  sleep 0.5
done
if ! kill -0 "$WW_PID" 2>/dev/null; then
  echo "wyoming-openwakeword died during startup. Log:" >&2
  tail -30 /tmp/wyoming-openwakeword.log >&2
  exit 1
fi

echo "[2/2] wyoming-satellite name=$SAT_ID listening on tcp://0.0.0.0:$SAT_PORT (hub dials in)"
echo "      Logs: /tmp/wyoming-openwakeword.log  (Ctrl+C to stop)"
exec "$SAT_VENV/bin/python" -m wyoming_satellite \
  --name "$SAT_ID" \
  --uri "tcp://0.0.0.0:$SAT_PORT" \
  --mic-command "parecord --raw --rate=16000 --format=s16le --channels=1" \
  --mic-command-rate 16000 \
  --mic-command-width 2 \
  --mic-command-channels 1 \
  --snd-command "paplay --raw --rate=22050 --format=s16le --channels=1" \
  --snd-command-rate 22050 \
  --snd-command-width 2 \
  --snd-command-channels 1 \
  --wake-uri "tcp://127.0.0.1:$WW_PORT" \
  --wake-word-name "$WAKE_WORD"