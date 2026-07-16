#!/usr/bin/env bash
set -euo pipefail

# Usage: scripts/provision-satellite-rs.sh <user@host> [mic-device]
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> [TTS_VOLUME=<pct>] \
#                    scripts/provision-satellite-rs.sh <user@host>
# Builds the static binary (via the fp16-shim-aware build script), copies it + the systemd
# unit to the Pi, and enables the service. A voice-only Pi needs only alsa-utils; a music Pi also
# gets snapclient + PipeWire — music, TTS and cues share ONE output, mixed by PipeWire (neither
# the bcm2835 jack nor an I2S DAC can ALSA-dmix). The output is auto-detected: an I2S DAC/amp
# HAT (HiFiBerry-class `sndrpihifiberry*` card, e.g. the MiniAmp driving a wired speaker) when
# present, else the 3.5mm jack. The mic card stays OUT of PipeWire, owned raw by the satellite.
# On music units the satellite's own playback (TTS + cues) additionally flows through a `tts`
# ALSA softvol (control "TTS" on the speaker card, set to TTS_VOLUME%, default 65) so the agent
# voice is calibrated independently of music — the volume knob for amp HATs that have none
# (e.g. the MiniAmp). Tune live: amixer -c <card> sset TTS <pct>% ; persist: sudo alsactl store.
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
# Two mic quirks are handled automatically:
#   - Jabra Speak2: its mic ADC firmware-sleeps when idle (capture-only opens EIO; deep sleep
#     ignores silence) — the unit's --wake-playback-ms plays a brief TONE through its speaker on
#     a cold open to wake it. Harmless for mics that don't sleep (they open first try, no tone).
#   - reSpeaker XVF3800: its capture engine only runs while its OWN playback stream is active
#     (both UAC endpoints are Synchronous off one internal clock) — capture-alone opens EIO and
#     a live capture dies the instant playback stops. A probe below detects this and installs
#     nabu-micclock.service, an endless zero-stream into the mic card's (unconnected) output
#     that keeps capture clocked 24/7; such units get --no-wake-playback (a tone adds nothing).

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
scp "${SSHOPTS[@]}" "$(dirname "$0")/../satellite/deploy/nabu-micclock.service" "$host:/tmp/"

# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM/TTS_VOLUME env vars: nothing is expanded locally; the
# remote bash evaluates everything (and reads vars from the command-prefix assignments).
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" TTS_VOLUME="${TTS_VOLUME:-65}" bash -se <<'EOF'
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
  # snd-usb-audio.conf is dead nrpacks=1 latency tuning (the parameter no longer exists; the
  # kernel logs "unknown parameter ignored").
  sudo rm -f /etc/modprobe.d/nabu-alsa.conf /etc/modprobe.d/snd-usb-audio.conf \
             /etc/udev/rules.d/99-nabu-jabra.rules
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

  # --- implicit-clock mic probe (reSpeaker XVF3800 class) ---
  # Some UAC mics only run capture while their OWN playback stream is active: the XVF3800's
  # endpoints are both Synchronous off one internal clock and its firmware gates the capture
  # engine on the output stream — capture-alone opens EIO on read, and a live capture dies the
  # instant playback stops (verified on-device). Probe capture-alone; if it fails but works
  # with a playback stream running, install nabu-micclock.service: an endless zero-stream into
  # the mic card's (unconnected) speaker output that keeps capture clocked 24/7.
  # (The Jabra fails differently: its ADC firmware-SLEEPS and ignores the silent probe stream
  # too — only a real-signal tone wakes it, which --wake-playback-ms handles — so it lands on
  # micclock=0 whether awake (capture-alone passes) or asleep (both probes fail). Correct both
  # ways.) Stop the consumers first: a running satellite/feeder holds the device and would fake
  # the probe result.
  # The verdict is STICKY: the probe is only valid on a COLD engine. After the feeder has been
  # running, the engine stays clocked for a while, so capture-alone succeeds and a re-provision
  # would wrongly tear the feeder down (observed on-device 2026-07-16: capture streamed for
  # minutes after the feeder stopped). If a prior provision installed nabu-micclock, trust it;
  # a hardware swap needs `sudo rm /etc/systemd/system/nabu-micclock.service` before re-running.
  sudo systemctl stop nabu-satellite nabu-micclock 2>/dev/null || true
  micclock=0
  if [ -f /etc/systemd/system/nabu-micclock.service ]; then
    echo "nabu-micclock already provisioned: keeping it (probe is unreliable on a warm engine)"
    micclock=1
  elif ! timeout 5 arecord -D "${dev}" -r 16000 -c 1 -f S16_LE -t raw -d 1 /dev/null 2>/dev/null; then
    timeout 10 aplay -D "${dev}" -r 16000 -c 2 -f S16_LE -t raw /dev/zero 2>/dev/null &
    feeder=$!
    sleep 0.5
    if timeout 5 arecord -D "${dev}" -r 16000 -c 1 -f S16_LE -t raw -d 1 /dev/null 2>/dev/null; then
      micclock=1
    fi
    kill "$feeder" 2>/dev/null || true
    wait "$feeder" 2>/dev/null || true
  fi
  if [ "$micclock" = 1 ]; then
    echo "Mic needs its own playback stream running (XVF3800-class): installing nabu-micclock"
    sudo sed "s|MICDEV|${dev}|g; s|%i|$user|g" /tmp/nabu-micclock.service \
      | sudo tee /etc/systemd/system/nabu-micclock.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable nabu-micclock.service
    sudo systemctl restart nabu-micclock.service
  else
    sudo systemctl disable --now nabu-micclock.service 2>/dev/null || true
    sudo rm -f /etc/systemd/system/nabu-micclock.service
  fi

  # --- output card: prefer an I2S DAC/amp HAT over the mic card / jack ---
  # HiFiBerry-class overlays (hifiberry-dac = MiniAmp, etc.) all register as sndrpihifiberry*.
  outcard=$(aplay -l | sed -nE 's/^card [0-9]+: (sndrpihifiberry[^ ]*) \[.*/\1/p' | sed -n '1p')
  if [ -n "$outcard" ]; then
    echo "Detected I2S DAC/amp HAT: $outcard (speaker output)"
    snddev="plughw:CARD=${outcard},DEV=0"
  else
    snddev="${dev}"   # no HAT: voice-only units play through the mic card (Jabra style)
  fi

  # --- music coexistence (opt-in via MUSIC_HUB) ---
  # Music + TTS + cues all share the speaker output ($snddev), mixed by PipeWire in userspace
  # (neither the bcm2835 jack nor an I2S DAC can ALSA-dmix; 2nd client -> -22). The mic card is
  # kept OUT of PipeWire and owned by the satellite via raw ALSA (capture + play-to-wake tone /
  # micclock feeder).
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

    # Keep the mic card OUT of PipeWire so the satellite owns it raw (capture + play-to-wake
    # tone / micclock feeder). Without this PipeWire grabs the card — it even elects the
    # XVF3800's unconnected speaker jack as default sink — and raw ALSA opens fail EBUSY.
    # Match the detected card by its distinctive name word. When an I2S DAC HAT is the speaker,
    # also disable the unused HDMI + headphone-jack cards so the HAT is deterministically the
    # ONLY sink WirePlumber can elect as default.
    sudo mkdir -p /etc/wireplumber/wireplumber.conf.d
    hatrules=""
    if [ -n "$outcard" ]; then
      hatrules='
  {
    matches = [
      { device.name = "~alsa_card.platform-.*hdmi.*" },
      { device.name = "~alsa_card.platform-.*mailbox.*" }
    ]
    actions = { update-props = { device.disabled = true } }
  }'
    fi
    sudo tee /etc/wireplumber/wireplumber.conf.d/50-nabu-exclude-mic.conf >/dev/null <<WPEOF
monitor.alsa.rules = [
  {
    matches = [ { device.name = "~alsa_card.*${excltoken}.*" } ]
    actions = { update-props = { device.disabled = true } }
  }${hatrules}
]
WPEOF

    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c <card> sset Music <pct>%)
    # -> PipeWire -> speaker. `tts` PCM: the satellite's own playback (TTS + cues) -> a softvol
    # holding the calibrated agent-voice level (TTS_VOLUME) -> PipeWire -> speaker, independent
    # of music — the volume knob for amp HATs that have none (e.g. the MiniAmp). Both CONTROLs
    # are stored on the speaker card (HAT when present, else the jack); the audio itself flows
    # through PipeWire either way. NO pcm.!default (PipeWire's own default stands); capture
    # stays direct plughw.
    outctl="${outcard:-Headphones}"
    sudo tee /etc/asound.conf >/dev/null <<ASOUND
pcm.music {
    type softvol
    slave.pcm "pipewire"
    control { name "Music" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}

pcm.tts {
    type softvol
    slave.pcm "pipewire"
    control { name "TTS" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
ASOUND

    # Apply the rules (the speaker becomes the only/default sink) and set a sane output volume.
    XDG_RUNTIME_DIR=/run/user/$uid systemctl --user restart wireplumber 2>/dev/null || true
    sleep 3
    XDG_RUNTIME_DIR=/run/user/$uid wpctl set-volume @DEFAULT_AUDIO_SINK@ 0.8 2>/dev/null || true

    # Calibrate the agent voice: a softvol CONTROL only materializes on first open of its PCM,
    # so play 1 s of silence through pcm.tts, then set the level (re-asserted on every provision,
    # like the 0.8 sink volume above) and store ALSA state so it survives a power cut, not just
    # a clean shutdown. Runs as the login user: on Raspberry Pi OS the default user is in the
    # `audio` group at login (needed to create the control); a first-ever provision under a user
    # only just added to `audio` above would fail here — re-login (rerun the script) fixes it.
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D tts -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset TTS "${TTS_VOLUME}%"
    sudo alsactl store

    # snapclient -> the `music` softvol PCM (so the satellite can duck it); XDGDIR so its
    # ALSA->PipeWire bridge connects.
    sudo sed "s|%i|$user|g; s|HUBHOST|${MUSIC_HUB}|g; s|ROOM|${MUSIC_ROOM:-$cardname}|g; s|XDGDIR|/run/user/$uid|g" \
      /tmp/snapclient.service | sudo tee /etc/systemd/system/snapclient.service >/dev/null
    sudo systemctl daemon-reload
    sudo systemctl enable snapclient.service
    sudo systemctl restart snapclient.service

    # nabu-satellite drop-in: TTS/cues -> the `tts` softvol (calibrated agent-voice level) ->
    # PipeWire (speaker); duck the `Music` softvol while active; XDG so aplay reaches PipeWire.
    # NO --keep-warm: PipeWire's exact drain re-arms
    # the mic only after playback truly finishes, so it never hears its own reply. Wake handling
    # is per-mic: the Jabra keeps the raw play-to-wake tone on the mic card; micclock (XVF3800)
    # units pass --no-wake-playback — the feeder owns capture clocking, a tone adds nothing.
    # Mic period: -F 20000 (20 ms, the satellite default) everywhere EXCEPT the Jabra, where
    # only the default ~125 ms period is proven reliable. Overrides the base voice ExecStart.
    if [ "$micclock" = 1 ]; then
      micperiod=" -F 20000"
      wakeargs="--no-wake-playback"
    else
      micperiod=""
      wakeargs="--wake-snd-command \"aplay -D ${dev} -r 22050 -c 1 -f S16_LE -t raw\" --wake-playback-ms 1000"
    fi
    sudo mkdir -p /etc/systemd/system/nabu-satellite.service.d
    sudo tee /etc/systemd/system/nabu-satellite.service.d/pipewire.conf >/dev/null <<DROPEOF
[Service]
Environment=XDG_RUNTIME_DIR=/run/user/$uid
ExecStart=
ExecStart=/usr/local/bin/nabu-satellite \\
  --listen 0.0.0.0:10700 \\
  --mic-command "arecord -D ${dev} -r 16000 -c 1 -f S16_LE -t raw${micperiod}" \\
  --snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw" \\
  ${wakeargs} \\
  --preroll-ms 1000 \\
  --music-mixer Music --music-card ${outctl} \\
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
