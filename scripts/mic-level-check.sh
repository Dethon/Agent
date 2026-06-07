#!/usr/bin/env bash
set -euo pipefail

# Quick mic-level probe for the WSL voice satellite. Records a few seconds from the
# same PulseAudio source the satellite uses (parecord / RDPSource) and reports the
# captured peak/RMS so we can tell whether the mic is silent, too quiet, or fine.
#
# Usage: ./scripts/mic-level-check.sh   (speak when it says SPEAK NOW)

export PULSE_SERVER="${PULSE_SERVER:-unix:/mnt/wslg/PulseServer}"
SECS="${SECS:-5}"
DEV="${DEV:-RDPSource}"
OUT=/tmp/mic-level-check.raw

echo "Source: $DEV   (hub SilenceGate speech threshold = RMS 500)"
for i in 3 2 1; do printf '\rStarting in %s...' "$i"; sleep 1; done
echo; echo ">>> SPEAK NOW (normal voice) for ${SECS}s <<<"
parecord --raw --rate=16000 --format=s16le --channels=1 --device="$DEV" "$OUT" &
REC=$!; sleep "$SECS"; kill "$REC" 2>/dev/null || true; wait "$REC" 2>/dev/null || true

python3 - "$OUT" <<'EOF'
import struct, math, sys
d = open(sys.argv[1], 'rb').read()
n = len(d) // 2
if n == 0:
    print("no samples captured"); raise SystemExit
s = struct.unpack('<%dh' % n, d[:n*2])
peak = max(abs(x) for x in s)
rms = math.sqrt(sum(x*x for x in s) / n)
fs = 32767
db = lambda v: 20*math.log10(v/fs + 1e-9)
print(f"\nduration {n/16000:.1f}s")
print(f"peak {peak:6d}  ({100*peak/fs:5.1f}% FS, {db(peak):6.1f} dBFS)")
print(f"rms  {rms:6.0f}  ({100*rms/fs:5.1f}% FS, {db(rms):6.1f} dBFS)")
if peak < 20:
    print("VERDICT: silence reaching WSL — Windows isn't sending the mic. Fix on Windows side.")
elif rms < 500:
    print("VERDICT: mic working but BELOW the speech gate (RMS<500) — too quiet, needs gain.")
elif rms < 2000:
    print("VERDICT: usable but on the quiet side — Whisper accuracy may suffer.")
else:
    print("VERDICT: healthy level.")
EOF
