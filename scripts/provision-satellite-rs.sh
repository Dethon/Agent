#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. The Pi needs only alsa-utils — no Python, no pipx.
#
# Audio addressing:
#   - No [mic-device] arg (the usual case): the USB audio card is auto-detected by NAME from
#     `arecord -l` and addressed as plughw:CARD=<name>,DEV=0. By-name addressing is immune to
#     ALSA slot/index churn — the old `options snd_usb_audio index=0` pinning collided with the
#     Pi 4/5 built-in vc4-hdmi + headphone cards (slots 0-2), failing the USB probe with -16 so
#     no capture card was created. The mic is 16 kHz mono native (what the satellite wants, no
#     resampling); `plughw` resamples only the 22050 Hz playback to a rate the device supports.
#   - With a [mic-device] arg: used verbatim on both commands. Use this for a device that is
#     already 16 kHz/22050-friendly, e.g. the reSpeaker 2-Mic HAT plughw:CARD=seeed2micvoicec,
#     DEV=0 (also add --button-gpio 17 --led-spi to ExecStart and dtparam=spi=on for the HAT).
#
# The unit passes --wake-playback-ms 500: the Jabra Speak2's mic ADC firmware-sleeps when idle
# and capture-only opens return EIO; from deep sleep it ignores silence too, so the satellite
# plays a brief TONE (real signal) through the speaker on a cold mic open to wake it. Harmless
# for mics that don't sleep (they open first try, no tone); pass --no-wake-playback to disable.

host=${1:?need user@host}
mic=${2:-}     # empty => auto-detect the USB audio card, address it by name

"$(dirname "$0")/../satellite/scripts/build-release.sh"
bin="$(dirname "$0")/../satellite/target/aarch64-unknown-linux-musl/release/nabu-satellite"

# Multiplex every ssh/scp below over ONE shared connection (ControlMaster), so password auth is
# entered exactly once instead of per-connection. The first command opens the master; the rest
# reuse the socket. The trap closes it (ControlPersist is just a safety net if the script dies).
ctl=$(mktemp -u "${TMPDIR:-/tmp}/nabu-ssh-XXXXXX")
SSHOPTS=(-o ControlMaster=auto -o ControlPath="$ctl" -o ControlPersist=120)
trap 'ssh -o ControlPath="$ctl" -O exit "$host" 2>/dev/null || true; rm -f "$ctl"' EXIT

ssh "${SSHOPTS[@]}" "$host" "sudo apt-get install -y alsa-utils"   # arecord/aplay only; no Python
scp "${SSHOPTS[@]}" "$bin" "$host:/tmp/nabu-satellite"
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/nabu-satellite.service" "$host:/tmp/"
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/snapclient.service" "$host:/tmp/"

# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM env vars: nothing is expanded locally; the remote
# bash evaluates everything (and reads vars from the command-prefix assignments).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" bash -se <<'EOF'
  set -euo pipefail

  if [ -n "${MUSIC_HUB:-}" ] && [ -n "${MIC}" ]; then
    echo "ERROR: music provisioning (MUSIC_HUB) supports only the auto-detected USB card; do not also pass a mic-device." >&2
    exit 1
  fi
  sudo install -m755 /tmp/nabu-satellite /usr/local/bin/nabu-satellite
  user=$(whoami)
  sudo usermod -aG gpio,input "$user"     # GPIO button + evdev/USB button access

  # Drop the old, broken index-pinning artifacts from any prior provisioning run, plus any
  # systemd drop-in (e.g. a temporary mic-command override) — it would shadow the ExecStart we
  # write here, so re-provisioning must clear it for the unit below to be authoritative.
  sudo rm -f /etc/modprobe.d/nabu-alsa.conf /etc/udev/rules.d/99-nabu-jabra.rules
  sudo rm -rf /etc/systemd/system/nabu-satellite.service.d

  if [ -z "${MIC}" ]; then
    # arecord -l lists only capture-capable cards, so the Pi's output-only vc4-hdmi/headphone
    # devices never appear — the USB mic is the lone entry on a dedicated satellite.
    al=$(arecord -l)
    cardname=$(printf '%s\n' "$al" | sed -nE 's/^card [0-9]+: ([^ ]+) \[.*/\1/p' | sed -n '1p')
    cardidx=$(printf '%s\n' "$al" | sed -nE 's/^card ([0-9]+):.*/\1/p' | sed -n '1p')
    if [ -z "$cardname" ]; then
      echo "ERROR: no USB capture device in 'arecord -l' — is the mic plugged in?" >&2
      exit 1
    fi
    echo "Detected USB audio card: $cardname (ALSA index $cardidx)"

    # Address the mic by NAME (stable across reboots / ALSA index churn). The capture side is
    # 16 kHz mono native — exactly what the satellite wants, no resampling; `plughw` resamples
    # only the 22050 Hz playback to a rate the device supports. No /etc/asound.conf: the earlier
    # 48 kHz plug device was wrong (this capture is 16 kHz-ONLY) — remove a stale one if present.
    sudo rm -f /etc/asound.conf
    dev="plughw:CARD=${cardname},DEV=0"

    # Stop USB autosuspend during long idle, keyed on the detected device's id. (This does NOT
    # fix the Jabra cold-start — its mic ADC firmware-sleeps; the satellite's --wake-playback-ms
    # play-to-wake handles that — but it's good hygiene for any USB-audio device.)
    usbid=$(cat /proc/asound/card${cardidx}/usbid 2>/dev/null || true)
    if [ -n "$usbid" ]; then
      printf 'ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="%s", ATTR{idProduct}=="%s", ATTR{power/control}="on"\n' \
        "${usbid%%:*}" "${usbid##*:}" | sudo tee /etc/udev/rules.d/99-nabu-usb-audio.rules >/dev/null
      sudo udevadm control --reload
      sudo udevadm trigger --action=add
    fi
  else
    dev="${MIC}"   # explicit device, used verbatim (e.g. reSpeaker 2-Mic HAT)
  fi

  # --- music coexistence (opt-in via MUSIC_HUB) ---
  snddev="${dev}"          # default: same device as capture (voice-only unit, unchanged)
  music_sed=(-e "/__MUSIC_FLAGS__/d")   # default: strip the placeholder line entirely
  if [ -n "${MUSIC_HUB:-}" ]; then
    sudo apt-get install -y snapclient
    snddev="duckmix"
    music_sed=(-e "s|__MUSIC_FLAGS__|--music-mixer Music --music-card ${cardname}|")

    # Shared dmix + softvol over the Jabra card, by NAME. NO pcm.!default: the capture command
    # keeps its explicit plughw device (capture never contended). ALL playback — TTS, cues,
    # keep-warm, and the play-to-wake tone (emitted on the playback path) — flows through
    # `duckmix` for music units; the tone remains full-scale (softvol is only on `music`).
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.duckmix {
    type plug
    slave.pcm {
        type dmix
        ipc_key 32421
        ipc_perm 0660
        # Pin a fine period/buffer. Without these, dmix hands clients a coarse ~125 ms period
        # (snapclient logs "Period time too small, changing from 20000 to 125000"), too coarse for
        # snapcast to track the server clock with inaudible single-sample corrections -> it falls
        # back to periodic audible hard-resyncs in the music. 1024-frame periods (~21 ms) restore
        # smooth sync; the satellite's own playback rides the same finer buffer (lower onset).
        slave {
            pcm "hw:CARD=${cardname},DEV=0"
            format S16_LE
            rate 48000
            channels 2
            period_size 1024
            buffer_size 8192
        }
    }
}
pcm.music {
    type plug
    slave.pcm {
        type softvol
        slave.pcm "duckmix"
        control { name "Music" card ${cardname} }
        min_dB -51.0
        max_dB 0.0
        resolution 256
    }
}
ASOUND

    # snapclient unit -> hub Snapcast :1704, output into the softvol PCM.
    sudo usermod -aG audio "$user"
    sudo sed "s|%i|$user|g; s|HUBHOST|${MUSIC_HUB}|g; s|ROOM|${MUSIC_ROOM:-$cardname}|g" \
      /tmp/snapclient.service | sudo tee /etc/systemd/system/snapclient.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable snapclient.service
    sudo systemctl restart snapclient.service
  else
    # downgrade / voice-only: tear down any prior music install so a re-provisioned
    # Pi returns to exactly the voice-only state (no orphaned restart-looping snapclient).
    sudo systemctl disable --now snapclient.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/snapclient.service
    sudo systemctl daemon-reload
  fi

  # Template the satellite unit: mic device stays plughw (capture untouched), snd device may be
  # duckmix (music) or plughw (voice), %i -> user, and the music flags line substituted/stripped.
  sudo sed -e "/--mic-command/ s#plughw:0,0#${dev}#" \
           -e "/--snd-command/ s#plughw:0,0#${snddev}#" \
           -e "s/%i/$user/g" \
           "${music_sed[@]}" \
           /tmp/nabu-satellite.service | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null

  # restart (not just enable --now): re-provisioning must pick up the new unit even when the
  # service is already running.
  sudo systemctl daemon-reload
  sudo systemctl enable nabu-satellite.service
  sudo systemctl restart nabu-satellite.service
  echo "Provisioned. Logs: journalctl -u nabu-satellite -f"
EOF
