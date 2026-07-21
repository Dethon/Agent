#!/usr/bin/env bash
# Records speaker-enrollment WAVs through the satellite's own mic (domain-matched to
# the AGC chain the identity gate hears) and drops them where the hub's voices volume
# can pick them up. Run ON the satellite Pi.
#
# Usage: enroll-voice.sh <name> [count] [scp-target]
#   name        identity folder (e.g. fran)
#   count       recordings to take (default 5)
#   scp-target  optional, e.g. pi5:/opt/jackbot/DockerCompose/volumes/voices
#               (omit to leave files in ./voices/<name>/ and copy manually)
set -euo pipefail

NAME="${1:?usage: enroll-voice.sh <name> [count] [scp-target]}"
COUNT="${2:-5}"
TARGET="${3:-}"
SECONDS_PER_TAKE=4
OUT="./voices/$NAME"
mkdir -p "$OUT"

CARD=$(arecord -l | awk -F'[:,]' '/^card /{gsub(/^ +| +$/, "", $2); split($2, a, " "); print a[1]; exit}')
[ -n "$CARD" ] || { echo "No capture card found (arecord -l)"; exit 1; }
echo "Recording from card: $CARD"

RESTART_SATELLITE=0
if systemctl is-active --quiet nabu-satellite; then
    echo "Stopping nabu-satellite (it holds the capture device); will restart when done."
    sudo systemctl stop nabu-satellite
    RESTART_SATELLITE=1
fi
trap '[ "$RESTART_SATELLITE" = 1 ] && sudo systemctl start nabu-satellite' EXIT

PHRASES=(
  "Di tu frase de activacion y una orden completa, con voz natural."
  "Pide el tiempo de manana como lo harias normalmente."
  "Pide que ponga tu musica favorita en el salon."
  "Pregunta que hora es y pide un temporizador de cinco minutos."
  "Di una frase larga cualquiera, como si hablaras con el asistente."
)

for i in $(seq 1 "$COUNT"); do
    idx=$(( (i - 1) % ${#PHRASES[@]} ))
    echo
    echo "[$i/$COUNT] ${PHRASES[$idx]}"
    for s in 3 2 1; do echo "  $s..."; sleep 1; done
    echo "  HABLA AHORA (${SECONDS_PER_TAKE}s)"
    arecord -q -D "plughw:CARD=$CARD,DEV=0" -f S16_LE -r 16000 -c 1 \
        -d "$SECONDS_PER_TAKE" "$OUT/enroll-$i.wav"
    echo "  guardado $OUT/enroll-$i.wav"
done

echo
if [ -n "$TARGET" ]; then
    echo "Copying to $TARGET/$NAME/"
    ssh "${TARGET%%:*}" "mkdir -p '${TARGET#*:}/$NAME'"
    scp "$OUT"/enroll-*.wav "$TARGET/$NAME/"
    echo "Done. Restart mcp-channel-voice (or wait for its next start) to rebuild profiles."
else
    echo "Recordings in $OUT — copy them to the hub's DockerCompose/volumes/voices/$NAME/"
fi
