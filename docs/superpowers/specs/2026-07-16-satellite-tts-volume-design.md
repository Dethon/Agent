# Satellite TTS/Cue Volume Calibration — Design

**Date:** 2026-07-16
**Status:** Approved

## Problem

The fran-office satellite plays through a HiFiBerry MiniAmp, which has no hardware volume
control. The agent's voice (TTS), the hub chime, and the satellite's cue beeps are too loud.
Music is fine: it already has its own volume path (Music Assistant → snapcast client volume,
plus the `Music` softvol used for ducking). The only source with no independent knob is the
satellite's own playback (TTS + cues), which plays at raw PipeWire sink level (0.8).

## Decision

Set-once calibration of **TTS + cues only**, leaving music untouched, implemented as a second
ALSA softvol PCM mirroring the existing `pcm.music` pattern. No Rust change, no hub change,
no .NET change — `scripts/provision-satellite-rs.sh` plus doc comments only.

### Rejected alternatives

- **Satellite `--volume` gain flag (Rust):** uniform across units, but needs a cross-compile +
  redeploy for a problem only PipeWire/HAT units have, and every later tweak means editing the
  systemd unit and restarting the service instead of one `amixer` command.
- **Hub-side per-satellite volume (`VoiceSettings`):** never touches the satellite's embedded
  cue WAVs — quiet TTS but loud beeps — and needs a hub redeploy per tweak.
- **Lowering the PipeWire sink volume:** trims music too; user wants music level untouched.

## Mechanism

1. **`pcm.tts` softvol** in `/etc/asound.conf` (music/PipeWire provisioning branch only),
   alongside `pcm.music`:

   ```
   pcm.tts {
       type softvol
       slave.pcm "pipewire"
       control { name "TTS" card ${outctl} }
       min_dB -51.0
       max_dB 0.0
       resolution 256
   }
   ```

2. **Drop-in snd command** becomes `--snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw"`.
   TTS, hub chime, and cue beeps all flow through it. snapclient/music is untouched; ducking
   still writes only the `Music` control.

3. **Materialize + set:** a softvol control only exists after its PCM is first opened, so
   provisioning plays ~0.3 s of `/dev/zero` through `pcm.tts` before
   `amixer -c ${outctl} sset TTS ${TTS_VOLUME}%`.

4. **`TTS_VOLUME` knob:** new env var on the provisioning command (like `MUSIC_HUB`), default
   **60%**. Provisioning re-asserts it on every run (authoritative re-provision, matching the
   existing sink `0.8`). Live calibration is one SSH command —
   `amixer -c sndrpihifiberry sset TTS 50%` — persisted across reboots by alsa-restore; the
   calibrated value gets baked into the script default afterwards.

5. **Scope:** lands in the generic music-unit path, so the laura-office jack unit gets the same
   `TTS` control (on its `Headphones` card) — harmless and equally useful. The Jabra voice-only
   path is untouched (hardware volume buttons).

## Failure modes

- A broken `pcm.tts` breaks satellite playback the same way a broken `pcm.music` breaks
  snapclient today — same file, same risk class, caught immediately on-device.
- The `amixer sset` runs only after the materializing silence play, so no "control not found"
  race.
- Wake tone / micclock feeder are raw ALSA on the mic card — untouched by this change.

## Verification (on-device, fran-office)

Re-provision, then:

- TTS reply audibly quieter; cue beeps quieter.
- Music level unchanged.
- Duck-while-speaking still ducks and restores.
- Reboot → TTS level persists (alsa-restore).

No repo test surface: this is a bash provisioning script; the repo has no test harness for
provisioning scripts.
