# Timers and alarms ring at max volume

**Date:** 2026-07-28
**Status:** Approved, not yet implemented

## Problem

A timer or alarm rings through the same attenuated path as ordinary agent speech, so a
satellite calibrated for comfortable conversation cannot ring loudly enough to be an alert.

On the deployed office unit the chain is:

| Stage | Effect |
|---|---|
| Hub `PcmGain` (ramp) | ≤ 1.0. Timers already pass `RampStartPercent = 100`; alarms ramp 50 % → 100 % over 4 rounds. |
| `AlarmTone.Amplitude = 0.5` | Earcon synthesized at half scale (−6 dB) while the spoken text that follows is at full TTS level. |
| `aplay -D tts` → `TTS` softvol | `TTS_VOLUME` = 75 % over a `min_dB -51 / max_dB 0` taper ⇒ **≈ −12.75 dB**, about 23 % of full amplitude. |
| PipeWire sink volume `0.8` | A second, device-wide cut on top. |

The `TTS` softvol *is* "per-satellite volume" — the documented per-unit voice calibration
knob (`amixer -c <card> sset TTS <pct>%`). It is the dominant term and it lives on the
satellite, so no hub-side gain change can fix this: the hub's audio is already at unity
there and boosting it would clip rather than get louder.

## Goal

Timers and alarms bypass the per-satellite voice calibration and ring at full scale,
without disturbing the music/voice balance or the ducking behaviour that the existing
softvol split exists to provide.

## Why three softvols, and why the master is the redundant one

The MiniAmp has no hardware volume, so every level is software. The existing structure is
a master plus two per-source knobs, and both source knobs are load-bearing:

- **PipeWire sink volume** — master over music + voice + cues. The only knob that moves
  everything.
- **`Music` softvol** — required by ducking. The satellite drops music to 20 % while
  listening/speaking; ducking the master instead would duck its own voice at the same time.
- **`TTS` softvol** — balances agent-voice loudness against music at the same master.
  Without it, "make the assistant quieter" also moves the music.

Collapsing `Music` and `TTS` breaks ducking; collapsing either into the master breaks the
music/voice balance. What *is* redundant is the **value** `0.8` on the master: a
provisioning-time "sane default" with no function, stacking a second attenuation under
knobs that already carry all the calibration. It moves to `1.0`.

## Design

### 1. Satellite level chain (provisioning)

`/etc/asound.conf` gains a third softvol beside `music` and `tts`:

```
pcm.alert {
    type softvol
    slave.pcm "pipewire"
    control { name "Alert" card <speaker> }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
```

`scripts/provision-satellite-rs.sh` changes:

- New env `ALERT_VOLUME`, default **100**.
- Materialize the control (a softvol control only exists after the first open of its PCM):
  1 s of silence through `pcm.alert`, then `amixer -c <card> sset Alert ${ALERT_VOLUME}%`,
  then `alsactl store` — the same three steps `TTS` already gets.
- Master: `wpctl set-volume @DEFAULT_AUDIO_SINK@` **`1.0`** (was `0.8`).
- The music drop-in passes
  `--alert-snd-command "aplay -D alert -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000"`,
  carrying the latency flags that `satellite/CLAUDE.md` requires when overriding devices
  (see *Companion fix* below).

Live-tunable per unit exactly like the other two knobs.

**Deployment note:** re-provisioning makes music and the agent voice audibly louder too,
not just alerts — the master stops eating headroom. `TTS_VOLUME` will want retuning
downward on the office unit afterwards. No compensating default is baked in, because
`wpctl`'s taper needs measuring on the box rather than assuming from arithmetic.

### 2. Wire protocol — `audio-start` gains `alert`

```json
{"type":"audio-start","data":{"rate":22050,"width":2,"channels":1,"timestamp":0,"alert":true}}
```

`PROTOCOL_VERSION` 1.4 → **1.5** (`satellite/src/wyoming/event.rs`, matched hub-side).

Compatible in both directions: an old satellite ignores the unknown field and plays the
alert through `tts` (today's behaviour); a new satellite treats a missing field as `false`.
Either deployment order is therefore safe.

`WyomingEvent::data_obj()` — carrying an `#[allow(dead_code)] // no production callers yet`
note since it was written — becomes its first production caller and loses the attribute.

### 3. Hub

- `PlaybackJob` gains `bool Alert = false`.
- `InsistentAnnouncementController.BuildJob` sets `Alert: true`. **This is the only
  producer**, and it maps exactly onto timers and alarms: `/api/voice/announce` routes to
  that controller if and only if the request carries `insistent`, which the HA alarm idiom
  mandates (`HomeAssistantPrompt`: "`insistent` must be present — omitting it makes a
  one-shot announce, not an alarm") and `TimerFireService` always sets. Ordinary
  announcements, download notifications and approval prompts stay on the normal path via
  `AnnouncementService`.
- `SatelliteSession.RunPlaybackLoopAsync`'s `onAudioStart` callback gains the flag.
- `WyomingSatelliteHost` writes `["alert"] = true` into the `audio-start` object when set.

`AnnouncePriority.High` is deliberately **not** reused as the marker — approval prompts and
`WyomingSatelliteHost`'s own high-priority job also use it, and they must not ring at alert
level.

### 4. Satellite playback pump

- New CLI flag `--alert-snd-command`, **defaulting to the value of `--snd-command`**. A
  voice-only unit has no `asound.conf` and no softvols, so it needs no provisioning change
  and behaves exactly as today.
- `PlaybackCmd::Start` carries `alert: bool`; `run_pump` holds both command strings and
  chooses the sink at Start. `PlaybackHandle::start` takes the flag.
- `state_machine.rs`: `"audio-start"` reads `alert` from the event data and passes it on.
- Cues keep using the normal command — they are voice-class earcons, not alerts.

The pump already serializes device access and opens/closes a sink per stream, so switching
PCM between streams cannot overlap or race for the exclusive device.

**Fallback:** if the alert sink fails to open, fall back to the normal sink for that stream
instead of reporting fatal. Playback-open errors are connection-fatal today, and "the alarm
dropped the hub connection" is a far worse failure than "the alarm was quiet".

### 5. Alarm tone level

`AlarmTone.Amplitude` 0.5 → **0.9**. A pure sine does not clip at high amplitude and the
existing 10 ms fade envelope keeps the segment edges click-free; 0.9 leaves a little
headroom rather than going to full scale under a mixer that may also be carrying ducked
music. This brings the attention-grabbing earcon up to roughly the level of the speech that
follows it.

### 6. Unchanged by design

- **The alarm wake-up ramp stays.** `InsistentDefaults.RampStartPercent = 50` /
  `RampRounds = 4` remain: alarms still fade in over the first four rounds, but now from a
  far louder ceiling. The change targets the routing floor, not the within-alert dynamics.
- **Music ducking is untouched.** An alert drives the satellite to `Speaking`, which already
  ducks music to `duck_percent`.

## Testing

Red-first for every item.

**Rust**

- `--alert-snd-command` parses; defaults to the `--snd-command` value *including when that
  is itself overridden*; unknown-arg rejection still holds.
- The pump writes to the alert command for `Start { alert: true }` and to the normal command
  otherwise — two `cat >> <file>` stand-ins, asserting which file received bytes.
- Cues always take the normal command, even while an alert stream is the most recent.
- An alert-sink open failure falls back to the normal sink and does **not** report fatal.
- `audio-start` without the `alert` field routes normally (back-compat).

**C#**

- An insistent job carries `Alert == true` for both `AnnounceKind.Timer` and
  `AnnounceKind.Alarm`.
- A plain `AnnouncementService` announce carries `Alert == false`.
- The emitted `audio-start` frame carries `"alert": true` only for alert jobs.
- `AlarmTone` peak sample pins the 0.9 amplitude.

Provisioning is shell and stays untested automatically; it is validated by re-running it on
the unit.

## Deployment

1. `satellite/scripts/build-release.sh` (never bare `cargo zigbuild`).
2. `scripts/provision-satellite-rs.sh <user@host>` on `speaker-fran-office` — writes
   `asound.conf`, sets the master to 1.0, creates and sets `Alert`, rewrites the drop-in.
3. Redeploy `mcp-channel-voice` on ai370.
4. Retune `TTS_VOLUME` on the unit if the voice is now too loud for conversation.

Protocol back-compat makes steps 1–3 order-independent.

## Companion fix — playback latency flags on the music drop-in

Unrelated to volume, fixed here because it touches the same drop-in line. **Its own commit.**

`92b9e9fa` (2026-06-11 perf pass) added `--start-delay=100000 -F 50000` to the playback
command: aplay's default start threshold is the *whole* 500 ms buffer, so streamed TTS isn't
audible until 500 ms has been synthesized and delivered — up to ~400 ms of dead air when the
first sentence is short. That commit touched only `satellite/src/config.rs` and
`satellite/deploy/nabu-satellite.service`, never `scripts/provision-satellite-rs.sh`.

Music units get a drop-in that **overrides the base ExecStart wholesale**, and its line is
`--snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw"` — so the fix is not active on
the deployed office satellite, against the `satellite/CLAUDE.md` invariant "keep them when
overriding devices". The mic half of the same perf pass *did* reach the drop-in (`f18b01c0`
inlined `-F 20000`), so this is an oversight rather than a decision. Nothing catches it:
`config.rs`'s `defaults_are_sane` asserts on the default string, which the drop-in replaces.

**Fix:** append `--start-delay=100000 -F 50000` to the drop-in's `--snd-command`.

**Expected effect is uncertain and that is accepted.** The base unit plays to `plughw`, where
the 500 ms-buffer reasoning holds directly. The drop-in plays into `pcm.tts` → softvol →
`pipewire`, and the PipeWire ALSA plugin negotiates buffering from the graph quantum, so it
may already be short and may not honour `start_threshold` identically. The change is harmless
either way and restores the documented invariant. Worth reading once on the unit —
`aplay -D tts -v` prints the negotiated `buffer_size` / `period_size` / `start_threshold` —
but the fix does not depend on the result.

Note this delay is invisible to hub metrics (`WakeToFirstAudioMs`,
`SpeechEndToFirstAudioMs`): it sits entirely downstream of the hub's last write.
