#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> \
#                    scripts/provision-satellite-rs.sh <user@host>
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. A voice-only Pi needs only alsa-utils; a music Pi also
# gets snapclient + PipeWire — music, TTS and cues share the 3.5mm jack (mixed by PipeWire, since
# the bcm2835 jack can't ALSA-dmix), while the Jabra stays the mic + play-to-wake tone (see below).
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
    carddesc=$(printf '%s\n' "$al" | sed -nE 's/^card [0-9]+: [^ ]+ \[([^]]+)\].*/\1/p' | sed -n '1p')
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
  # Music + TTS + cues all share the 3.5mm JACK, mixed by PipeWire in userspace. The bcm2835 jack's
  # ALSA dmix can't share the device (2nd client -> -22), so a sound server does the mixing. The
  # Jabra is kept OUT of PipeWire and owned by the satellite via raw ALSA (mic + play-to-wake tone).
  snddev="${dev}"   # base unit stays voice-style; music units add a drop-in that overrides ExecStart
  # Clear any stale music drop-in first so a re-provision is deterministic (added back below if music).
  sudo rm -f /etc/systemd/system/nabu-satellite.service.d/pipewire.conf
  if [ -n "${MUSIC_HUB:-}" ]; then
    uid=$(id -u "$user")
    excltoken=$(printf '%s' "$carddesc" | awk '{print $1}')   # distinctive card word, e.g. "Jabra"
    # snapclient + PipeWire (the userspace mixer). snapclient + the satellite are SYSTEM services
    # with no login session, so enable lingering to run the user's PipeWire at boot and pass
    # XDG_RUNTIME_DIR into both units so their aplay/snapclient reach it.
    sudo apt-get install -y snapclient pipewire pipewire-alsa wireplumber
    sudo usermod -aG audio "$user"
    sudo loginctl enable-linger "$user"
    XDG_RUNTIME_DIR=/run/user/$uid systemctl --user enable --now pipewire pipewire-pulse wireplumber 2>/dev/null || true

    # Keep the mic card OUT of PipeWire so the satellite owns it raw (mic capture + the play-to-wake
    # tone). Without this PipeWire grabs the Jabra sink and the raw wake-tone aplay fails EBUSY, so
    # the firmware-sleeping mic never wakes. Match the detected card by its distinctive name word.
    sudo mkdir -p /etc/wireplumber/wireplumber.conf.d
    sudo tee /etc/wireplumber/wireplumber.conf.d/50-nabu-exclude-mic.conf >/dev/null <<WPEOF
monitor.alsa.rules = [
  {
    matches = [ { device.name = "~alsa_card.*${excltoken}.*" } ]
    actions = { update-props = { device.disabled = true } }
  }
]
WPEOF

    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c Headphones sset Music <pct>%)
    # -> PipeWire -> jack. NO pcm.!default (PipeWire's own default stands); capture stays direct plughw.
    sudo tee /etc/asound.conf >/dev/null <<'ASOUND'
pcm.music {
    type softvol
    slave.pcm "pipewire"
    control { name "Music" card Headphones }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
ASOUND

    # Apply the exclusion (jack becomes the only/default sink) and set a sane jack volume.
    XDG_RUNTIME_DIR=/run/user/$uid systemctl --user restart wireplumber 2>/dev/null || true
    sleep 3
    XDG_RUNTIME_DIR=/run/user/$uid wpctl set-volume @DEFAULT_AUDIO_SINK@ 0.8 2>/dev/null || true

    # snapclient -> the `music` softvol PCM (so the satellite can duck it); XDGDIR so its
    # ALSA->PipeWire bridge connects.
    sudo sed "s|%i|$user|g; s|HUBHOST|${MUSIC_HUB}|g; s|ROOM|${MUSIC_ROOM:-$cardname}|g; s|XDGDIR|/run/user/$uid|g" \
      /tmp/snapclient.service | sudo tee /etc/systemd/system/snapclient.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable snapclient.service
    sudo systemctl restart snapclient.service

    # nabu-satellite drop-in: TTS/cues -> PipeWire (jack); play-to-wake tone -> the mic card (raw
    # ALSA, --wake-snd-command); duck the `Music` softvol while active; XDG so aplay -D pipewire
    # connects. NO --keep-warm: PipeWire's exact drain re-arms the mic only after playback truly
    # finishes, so it never hears its own reply on the loud jack. Overrides the base voice ExecStart.
    sudo mkdir -p /etc/systemd/system/nabu-satellite.service.d
    sudo tee /etc/systemd/system/nabu-satellite.service.d/pipewire.conf >/dev/null <<DROPEOF
[Service]
Environment=XDG_RUNTIME_DIR=/run/user/$uid
ExecStart=
ExecStart=/usr/local/bin/nabu-satellite \\
  --listen 0.0.0.0:10700 \\
  --mic-command "arecord -D ${dev} -r 16000 -c 1 -f S16_LE -t raw" \\
  --snd-command "aplay -D pipewire -r 22050 -c 1 -f S16_LE -t raw" \\
  --wake-snd-command "aplay -D ${dev} -r 22050 -c 1 -f S16_LE -t raw" \\
  --preroll-ms 1000 \\
  --wake-playback-ms 1000 \\
  --music-mixer Music --music-card Headphones \\
  --threshold 0.7 \\
  --trigger-level 2
DROPEOF
  else
    # downgrade / voice-only: tear down any prior music install so a re-provisioned Pi returns to
    # exactly the voice-only state (no orphaned snapclient / stale PipeWire routing config).
    sudo systemctl disable --now snapclient.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/snapclient.service /etc/asound.conf
    sudo rm -f /etc/wireplumber/wireplumber.conf.d/50-nabu-exclude-mic.conf
    sudo systemctl daemon-reload
  fi

  # Template the base (voice) unit: mic + snd devices -> the detected plughw, %i -> user. Music units
  # additionally carry the pipewire.conf drop-in written above, which overrides this ExecStart to
  # route TTS/cues to PipeWire and keep the wake tone on the mic card.
  sudo sed -e "/--mic-command/ s#plughw:0,0#${dev}#" \
           -e "/--snd-command/ s#plughw:0,0#${snddev}#" \
           -e "s/%i/$user/g" \
           /tmp/nabu-satellite.service | sudo tee /etc/systemd/system/nabu-satellite.service >/dev/null

  # restart (not just enable --now): re-provisioning must pick up the new unit even when the
  # service is already running.
  sudo systemctl daemon-reload
  sudo systemctl enable nabu-satellite.service
  sudo systemctl restart nabu-satellite.service
  echo "Provisioned. Logs: journalctl -u nabu-satellite -f"
EOF
