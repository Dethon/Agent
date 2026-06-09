#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. The Pi needs only alsa-utils — no Python, no pipx.

host=${1:?need user@host}
mic=${2:-plughw:CARD=seeed2micvoicec,DEV=0}

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
  sudo sed "s/%i/\$user/g; s#seeed2micvoicec,DEV=0#${mic#*CARD=}#g" /tmp/nabu-satellite.service \
    | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable --now nabu-satellite.service
  echo "Logs: journalctl -u nabu-satellite -f"
EOF

# Jabra Speak only: also force the USB card to ALSA index 0 to avoid the #263 high-pitch bug —
#   echo -e 'options snd_usb_audio index=0\noptions snd_bcm2835 index=1' | sudo tee /etc/modprobe.d/nabu-alsa.conf
# and pass --button-evdev /dev/input/eventX:<keycode> (USB foot-switch) or wire a GPIO button.
#
# Jabra Speak2 55/75 only: stop USB autosuspend from suspending the device mid-stream —
#   echo 'ACTION=="add", SUBSYSTEM=="usb", ATTR{idVendor}=="0b0e", ATTR{power/control}="on"' \
#     | sudo tee /etc/udev/rules.d/99-nabu-jabra.rules && sudo udevadm control --reload