#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <satellite-id> <hub-host> <wake-word> [mic-device] [button-gpio]"
  echo "  e.g.: $0 kitchen-01 hub.local hey_jarvis plughw:CARD=seeed2micvoicec,DEV=0 5"
  exit 64
fi

satellite_id=$1
hub_host=$2
wake_word=$3
mic_device=${4:-plughw:CARD=seeed2micvoicec,DEV=0}
button_gpio=${5:-}

echo ">> Updating apt"
sudo apt-get update
sudo apt-get install -y python3 python3-pip pipx alsa-utils sox libportaudio2

echo ">> Installing Wyoming satellite stack"
pipx ensurepath
pipx install wyoming-satellite || pipx upgrade wyoming-satellite
pipx install wyoming-openwakeword || pipx upgrade wyoming-openwakeword

unit_dir=/etc/systemd/system
sat_unit=$unit_dir/wyoming-satellite.service
ww_unit=$unit_dir/wyoming-openwakeword.service

button_args=""
if [[ -n "$button_gpio" ]]; then
  button_args="--awake-wav /usr/share/sounds/alsa/Front_Center.wav --done-wav /usr/share/sounds/alsa/Side_Right.wav --gpio-button $button_gpio"
fi

sudo tee "$ww_unit" >/dev/null <<EOF
[Unit]
Description=Wyoming openWakeWord
After=network-online.target

[Service]
ExecStart=$(command -v wyoming-openwakeword) --uri tcp://0.0.0.0:10400 --preload-model $wake_word
Restart=always
User=$USER

[Install]
WantedBy=multi-user.target
EOF

sudo tee "$sat_unit" >/dev/null <<EOF
[Unit]
Description=Wyoming Satellite ($satellite_id)
After=wyoming-openwakeword.service
Requires=wyoming-openwakeword.service

[Service]
ExecStart=$(command -v wyoming-satellite) \\
  --name $satellite_id \\
  --uri tcp://0.0.0.0:10700 \\
  --mic-command "arecord -D $mic_device -r 16000 -c 1 -f S16_LE -t raw" \\
  --snd-command "aplay -D $mic_device -r 22050 -c 1 -f S16_LE -t raw" \\
  --wake-uri tcp://127.0.0.1:10400 \\
  --wake-word-name $wake_word \\
  --vad webrtcvad \\
  --event-uri tcp://$hub_host:10700 \\
  $button_args
Restart=always
User=$USER

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now wyoming-openwakeword.service wyoming-satellite.service

echo "Satellite '$satellite_id' provisioned. Logs:"
echo "  journalctl -u wyoming-satellite -f"
