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
# Defaults match `McpChannelVoice/appsettings.Development.json` (fran-office-01) —
# change SAT_ID to one of {fran-office-01, laura-office-01} or edit appsettings to
# add a new satellite (and give it an Address so the hub dials it).
#
# Usage:
#   ./scripts/wsl-satellite.sh
#   SAT_ID=laura-office-01 SAT_PORT=10600 ./scripts/wsl-satellite.sh

SAT_ID="${SAT_ID:-fran-office-01}"
WAKE_WORD="${WAKE_WORD:-ok_nabu}"
SAT_PORT="${SAT_PORT:-10700}"
WW_PORT="${WW_PORT:-10400}"
WW_THRESHOLD="${WW_THRESHOLD:-0.5}"
WW_DEBUG="${WW_DEBUG:-0}"
# Linear gain applied to mic audio inside wyoming-satellite, BEFORE wake-word
# detection and before streaming to the hub/Whisper. WSLg's RDPSource bridge caps
# at 0 dB, so a quiet Windows mic arrives quiet; measured ~RMS 716 / -12.5 dBFS peak
# here, leaving ~4x of headroom. 3.0 lifts speech to a healthy ~RMS 2150 without
# clipping. Set MIC_GAIN=1.0 to disable. Re-measure with scripts/mic-level-check.sh.
MIC_GAIN="${MIC_GAIN:-3.0}"

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

WW_PID=
SAT_PID=
cleanup() {
  trap - EXIT INT TERM
  for pid in "${SAT_PID:-}" "${WW_PID:-}"; do
    [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
  done
  for pid in "${SAT_PID:-}" "${WW_PID:-}"; do
    [[ -n "$pid" ]] && wait "$pid" 2>/dev/null || true
  done
}
trap cleanup EXIT INT TERM

# True when something is LISTENing on $1. We check ss state rather than opening a
# connection: WSL2 *mirrored* networking makes a loopback connect to a port with no
# listener HANG instead of refusing, so the old `/dev/tcp` probe never returned and
# the script wedged before printing "[1/2]". A LISTEN-state check is instant in both
# NAT and mirrored modes.
port_listening() {
  [[ -n "$(ss -ltnH "sport = :$1" 2>/dev/null)" ]]
}

# Return 0 once nothing is listening on $1, escalating to SIGKILL if a stale
# instance refuses to release the port within ~5s.
wait_port_free() {
  local port="$1"
  for _ in $(seq 1 20); do
    port_listening "$port" || return 0
    sleep 0.25
  done
  pkill -9 -u "$USER" -f wyoming_openwakeword 2>/dev/null || true
  pkill -9 -u "$USER" -f wyoming_satellite 2>/dev/null || true
  sleep 0.5
}

# Wipe any stale instances from a prior run. Historically the satellite was exec'd,
# which discarded this script's EXIT trap and orphaned the backgrounded openwakeword
# whenever the satellite died without a process-group signal (kill-by-PID, crash,
# OOM) — leaving port 10400 held. The satellite now runs as a child (see below) so
# cleanup always fires, but we still defensively wipe and wait for the ports to free
# rather than racing a fixed sleep. Quiet — only kills our own.
pkill -u "$USER" -f wyoming_openwakeword 2>/dev/null || true
pkill -u "$USER" -f wyoming_satellite 2>/dev/null || true
wait_port_free "$WW_PORT"
wait_port_free "$SAT_PORT"

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
  if port_listening "$WW_PORT"; then
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
# Run as a child (NOT exec) so the EXIT/INT/TERM trap above survives and always
# reaps openwakeword when the satellite stops for ANY reason.
"$SAT_VENV/bin/python" -m wyoming_satellite \
  --name "$SAT_ID" \
  --uri "tcp://0.0.0.0:$SAT_PORT" \
  --mic-command "parecord --raw --rate=16000 --format=s16le --channels=1" \
  --mic-command-rate 16000 \
  --mic-command-width 2 \
  --mic-command-channels 1 \
  --mic-volume-multiplier "$MIC_GAIN" \
  --snd-command "paplay --raw --rate=22050 --format=s16le --channels=1" \
  --snd-command-rate 22050 \
  --snd-command-width 2 \
  --snd-command-channels 1 \
  --wake-uri "tcp://127.0.0.1:$WW_PORT" \
  --wake-word-name "$WAKE_WORD" &
SAT_PID=$!
wait "$SAT_PID" || true
