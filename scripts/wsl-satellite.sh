#!/usr/bin/env bash
set -euo pipefail

# Run the Rust nabu-satellite on this WSL host, routing audio through WSLg PulseAudio.
#
# Topology: nabu-satellite is a Wyoming *server* listening on --listen (SAT_PORT). The
# McpChannelVoice hub (in Docker) dials OUT to it as a client — see the satellite's
# Address in McpChannelVoice/appsettings.Development.json, which points at
# tcp://host.docker.internal:SAT_PORT. Wake detection ("ok nabu") runs EMBEDDED in the
# binary (tract) — no wyoming-openwakeword side process, no pipx, no Python venvs.
#
# Defaults match `McpChannelVoice/appsettings.Development.json`: port 10700 is
# fran-office-01; SAT_PORT=10600 is laura-office-01. The hub identifies a satellite
# purely by which address it dialed — the binary takes no name.
#
# Usage:
#   ./scripts/wsl-satellite.sh
#   SAT_PORT=10600 MIC_GAIN=2.0 THRESHOLD=0.6 ./scripts/wsl-satellite.sh

SAT_PORT="${SAT_PORT:-10700}"
THRESHOLD="${THRESHOLD:-0.5}"
# Linear gain applied to mic audio BEFORE wake detection and streaming to the hub.
# WSLg's RDPSource bridge caps at 0 dB, so a quiet Windows mic arrives quiet; measured
# ~RMS 716 / -12.5 dBFS peak here, leaving ~4x of headroom. 3.0 lifts speech to a
# healthy ~RMS 2150 without clipping. The binary has no gain flag, so the gain rides
# in a python audioop pipe inside the mic command; MIC_GAIN=1.0 drops the pipe.
# Re-measure with scripts/mic-level-check.sh.
MIC_GAIN="${MIC_GAIN:-3.0}"

repo="$(cd "$(dirname "$0")/.." && pwd)"
bin="$repo/satellite/target/release/nabu-satellite"

if [[ -z "${PULSE_SERVER:-}" ]] && [[ ! -S /mnt/wslg/PulseServer ]]; then
  echo "PulseAudio not detected. Are you running inside WSLg?" >&2
  echo "Try: pactl info" >&2
  exit 1
fi

mic_cmd="parecord --raw --rate=16000 --format=s16le --channels=1"
if [[ "$MIC_GAIN" != "1.0" && "$MIC_GAIN" != "1" ]]; then
  if ! python3 -c "import audioop" 2>/dev/null; then
    echo "python3 with audioop is required for MIC_GAIN != 1.0 (audioop was removed in" >&2
    echo "Python 3.13 — use MIC_GAIN=1.0 or a python <= 3.12)." >&2
    exit 1
  fi
  mic_cmd="$mic_cmd | python3 -u -c \"
import sys, audioop
r, w = sys.stdin.buffer, sys.stdout.buffer
while b := r.read1(4096):
    w.write(audioop.mul(b, 2, $MIC_GAIN))
\""
fi

( cd "$repo/satellite" && cargo build --release )

# True when something is LISTENing on $1. We check ss state rather than opening a
# connection: WSL2 *mirrored* networking makes a loopback connect to a port with no
# listener HANG instead of refusing, so a `/dev/tcp` probe never returns. A
# LISTEN-state check is instant in both NAT and mirrored modes.
port_listening() {
  [[ -n "$(ss -ltnH "sport = :$1" 2>/dev/null)" ]]
}

# Wipe any stale instance from a prior run, escalating to SIGKILL if it refuses to
# release the port within ~5s.
pkill -u "$USER" -x nabu-satellite 2>/dev/null || true
for _ in $(seq 1 20); do
  port_listening "$SAT_PORT" || break
  sleep 0.25
done
if port_listening "$SAT_PORT"; then
  pkill -9 -u "$USER" -x nabu-satellite 2>/dev/null || true
  sleep 0.5
fi

echo "nabu-satellite listening on tcp://0.0.0.0:$SAT_PORT (hub dials in; threshold=$THRESHOLD mic-gain=$MIC_GAIN)"
# --latency-msec=50 is mandatory on WSLg: the RDP sink's default stream buffer adds
# ~1.6 s of playback latency per stream, which swallows the hub's follow-up window.
exec env RUST_LOG="${RUST_LOG:-info}" "$bin" \
  --listen "0.0.0.0:$SAT_PORT" \
  --threshold "$THRESHOLD" \
  --mic-command "$mic_cmd" \
  --snd-command "paplay --raw --rate=22050 --format=s16le --channels=1 --latency-msec=50"