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
# and capture-only opens return EIO; the satellite plays a brief silent buffer through the
# speaker to wake it before opening the mic. Harmless for mics that don't sleep (they catch on
# the first attempt); pass --no-wake-playback in ExecStart for a device that never needs it.

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

# Quoted heredoc + MIC env var: nothing is expanded locally; the remote bash evaluates
# everything (and reads MIC from the command-prefix assignment).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" bash -se <<'EOF'
  set -euo pipefail
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

  sudo sed "s/%i/$user/g; s#plughw:0,0#${dev}#g" /tmp/nabu-satellite.service \
    | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null

  # restart (not just enable --now): re-provisioning must pick up the new unit even when the
  # service is already running.
  sudo systemctl daemon-reload
  sudo systemctl enable nabu-satellite.service
  sudo systemctl restart nabu-satellite.service
  echo "Provisioned. Logs: journalctl -u nabu-satellite -f"
EOF
