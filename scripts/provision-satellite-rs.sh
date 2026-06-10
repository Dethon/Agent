#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. The Pi needs only alsa-utils — no Python, no pipx.
#
# Default [mic-device] targets a Jabra Speak2 (55/75): plughw:0,0, made deterministic by the
# Jabra-only steps below (snd_usb_audio pinned to ALSA index 0 — also the fix for the #263
# high-pitch bug; plus a udev rule stopping USB autosuspend mid-stream). The card NAME is
# variant-dependent (75->J75, 55 MS->MS, 55 UC->UC), hence index-pinning over a name.
# reSpeaker 2-Mic HAT: pass plughw:CARD=seeed2micvoicec,DEV=0 and add
# --button-gpio 17 --led-spi to the unit's ExecStart (APA102s need dtparam=spi=on).

host=${1:?need user@host}
mic=${2:-plughw:0,0}

"$(dirname "$0")/../satellite/scripts/build-release.sh"
bin="$(dirname "$0")/../satellite/target/aarch64-unknown-linux-musl/release/nabu-satellite"

ssh "$host" "sudo apt-get install -y alsa-utils"   # arecord/aplay only; no Python
scp "$bin" "$host:/tmp/nabu-satellite"
scp "$(dirname "$0")/../satellite/deploy/nabu-satellite.service" "$host:/tmp/"
ssh "$host" bash -se <<EOF
  set -euo pipefail
  sudo install -m755 /tmp/nabu-satellite /usr/local/bin/nabu-satellite
  user=\$(whoami)
  sudo usermod -aG gpio,input "\$user"     # GPIO button + evdev/USB button access
  sudo sed "s/%i/\$user/g; s#plughw:0,0#${mic}#g" /tmp/nabu-satellite.service \
    | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null
  if [ "${mic}" = "plughw:0,0" ]; then
    printf 'options snd_usb_audio index=0\noptions snd_bcm2835 index=1\n' \
      | sudo tee /etc/modprobe.d/nabu-alsa.conf >/dev/null
    echo 'ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="0b0e", ATTR{power/control}="on"' \
      | sudo tee /etc/udev/rules.d/99-nabu-jabra.rules >/dev/null
    sudo udevadm control --reload
    echo "Jabra: snd_usb_audio pinned to ALSA card 0 (takes effect on reboot or USB replug)"
  fi
  sudo systemctl daemon-reload
  sudo systemctl enable --now nabu-satellite.service
  echo "Logs: journalctl -u nabu-satellite -f"
EOF