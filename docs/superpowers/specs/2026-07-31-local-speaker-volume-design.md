# Local speaker volume by voice

Date: 2026-07-31

## Goal

Let a person change the physical output level of the satellite they are standing next to, by
voice, without the transcript ever reaching the agent or an LLM.

This is the speaker's own master level — the knob a HiFiBerry MiniAmp does not have in hardware.
It scales everything the speaker plays: music, the agent's replies, cues and alarms alike.

Music volume is a different thing and is **out of scope**. "Sube el volumen" keeps going to the
agent, which sets the Music Assistant player level through Home Assistant
(`Domain/Prompts/HomeAssistantPrompt.cs` lists `volume_set.sh` on every media_player). Nothing in
this design touches that path.

## Why a hub fast-path and not an on-device wake model

The satellite already runs the openWakeWord pipeline in process, and its melspectrogram and
embedding stages are the expensive ones. A second classifier head reading the same `emb_buf`
would cost almost nothing at runtime, so bare wake-free command phrases are technically cheap.

We are not doing that in v1. Each phrase needs its own trained model, and `satellite/CLAUDE.md`
already records music itself triggering the wake word at a low threshold — bare command phrases
would need context gating to stay safe. Matching the transcript on the hub needs no models, takes
any phrasing, and is changed by editing a config file.

The cost is latency: a command runs after wake, speech and STT, so roughly one to two seconds.
That is acceptable for a volume step.

## Which knob

`scripts/provision-satellite-rs.sh` sets up four levels on a music unit:

- Three ALSA softvols on the speaker card — `Music`, `TTS`, `Alert` — each on a −51 dB taper.
- A master, which is **not** an amixer control: it is the PipeWire sink, set with
  `wpctl set-volume @DEFAULT_AUDIO_SINK@ 1.0` and currently pinned at full.

The master is the target. Three consequences follow, and they are what make this design small:

- **The ducker is untouched.** `satellite/src/music.rs` writes the `Music` softvol. The master is
  an independent control and the two multiply, so ducking keeps working unchanged and
  `DuckGuard`'s synchronous restore-to-100 stays correct.
- **There is no level state to own.** `wpctl` takes relative steps (`10%+`, `10%-`), so volume
  up and down carry no satellite state at all.
- **Persistence is free.** Wireplumber stores the sink volume in its own state, so a level
  survives a satellite restart and a reboot without us writing anything.

Because it is the master, turning the speaker down also turns alarms down. That is what a
physical volume knob does, and it is accepted. Mute is the case where it is not acceptable — see
below.

## Hub

### Matching

New `VoiceCommandMatcher` in `McpChannelVoice/Services/`. Pure, no I/O.

Normalization, applied to both the transcript and the configured phrases: lowercase, strip
accents, strip punctuation, collapse whitespace. Phrases can therefore be written unaccented in
config and still match Whisper's accented output.

Matching is **whole-transcript only**. "Sube el volumen local" matches; "sube el volumen local y
apaga la luz" does not, and goes to the agent. This is what stops the fast-path from swallowing a
compound request.

Every phrase carries an explicit local marker, so nothing a person says about music volume can
match by accident.

### Where it hooks in

`TranscriptDispatcher.DispatchAsync`, at one point: **after** the gibberish gate, **before**
`GetOrCreateAsync`.

- After the gate, because a low-quality transcript should be dropped, not acted on. A
  transcript that fails the quality bar is dropped even if its text matches a phrase.
- Before `GetOrCreateAsync`, because that is a full `create_conversation` MCP round trip. Matching
  first keeps the path fast and keeps volume commands out of conversation history.

A match executes the command, publishes a `VoiceEvent` with `Outcome = "command"`, and returns
`false`. That return value already means "nothing reached the agent": `FollowUpConversation` sees
it and calls `EndConversation`, which writes the closing `transcript` and re-arms the satellite.
No new turn-end plumbing.

**Known limitation, accepted for v1:** a command spoken inside an open follow-up window ends that
conversation, because `false` ends the turn. Keeping the conversation alive would need a third
outcome in that loop, which is deliberately out of scope.

### Reaching the satellite

`SatelliteSession` today exposes a playback channel but no way to write a control event — the
Wyoming client lives inside `WyomingSatelliteHost`'s per-connection scope. It gains one:

```
Func<WyomingEvent, CancellationToken, Task>? ControlWriter
```

set by the host at connection setup alongside the callbacks it already builds
(`EndConversation`, `SpeechStopped`, `ListeningStarted`), and cleared on teardown. A null writer
means the satellite is not connected, so the command is logged and skipped.

It goes on the session rather than being threaded through the dispatcher because
`InsistentAnnouncementController` needs the same write path for the alert hold, and it already
holds sessions via `OnlineSessions(targetIds)`. One surface serves both, and every future
fast-path command as well.

### Alert hold

`InsistentAnnouncementController.RunLoopAsync` sends `alert-hold` before its `while` loop and
`alert-release` in its existing `finally`. That one boundary covers every way an alarm ends —
dismissal, cancellation, max repeats, max duration.

The hold has to come from the hub. `AnnounceSettings.GapSeconds` defaults to 15 s between rounds
but is overridable per request, so a satellite-side timer waiting out the gap would be guessing at
a number the hub owns.

## Wire

Protocol 1.7 → 1.8. The version is one constant shared by both ends
(`satellite/src/wyoming/event.rs` and `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs`)
and both move together, with the existing version tests updated.

One new hub → satellite event:

```
speaker-volume  {"action": "up" | "down" | "mute" | "unmute" | "alert-hold" | "alert-release"}
```

The hub sends intent, never numbers. Step size lives on the satellite, next to the hardware it
applies to, for the same reason `TTS_VOLUME` and `ALERT_VOLUME` live in provisioning.

An unrecognised `action` is warned about and ignored, so a newer hub cannot break an older
satellite.

## Satellite

### Configuration

- `--volume-sink <name>` — the PipeWire sink to drive, e.g. `@DEFAULT_AUDIO_SINK@`. Absent means
  the feature is off: a `speaker-volume` event is a no-op with a warning. This mirrors
  `music_mixer: None` disabling ducking. Provisioning sets it only on music units, because
  PipeWire is only installed there.
- `--volume-step <pct>` — percentage points of the sink's range per step, default 10.

The satellite service already has `XDG_RUNTIME_DIR` threaded in by provisioning, which it needs
for `aplay` through PipeWire, so `wpctl` can reach the session bus.

### State

- `user_muted: bool` — process-scoped. When a sink is configured, seeded once at startup from
  `wpctl get-volume <sink>`, whose output carries `[MUTED]` when the sink is muted. If the read
  fails it defaults to false. Updated by `mute` and `unmute`.
- `held: bool` — per-connection.

Volume up and down hold no state.

### Actions

| Action | Effect |
| --- | --- |
| `up` | `wpctl set-volume -l 1.0 <sink> <step>%+`, then cue |
| `down` | `wpctl set-volume -l 1.0 <sink> <step>%-`, then cue |
| `mute` | `user_muted = true`; cue, then `wpctl set-mute <sink> 1` once the cue has drained |
| `unmute` | `user_muted = false`; `wpctl set-mute <sink> 0`, then cue |
| `alert-hold` | `held = true`; if `user_muted`, unmute the sink. No cue |
| `alert-release` | if `held`, reapply `user_muted` to the sink; `held = false`. No cue |

`-l 1.0` caps at 100 %, so repeated `up` cannot push the sink above unity.

Cue ordering is what makes the confirmation audible in both directions, and mute needs care:
muting the sink silences an in-flight cue immediately, so the mute is applied on the cue's **drain
completion**, which the playback pump already reports. If the cue is dropped because a stream is
active, the mute applies at once — there is nothing to wait for.

`alert-release` does nothing unless a hold is outstanding, so a stray or duplicated release can
never change the mute state on its own.

An explicit `mute` during a hold applies immediately. The later `alert-release` then reasserts
`user_muted = true`, which is the same state, so the two rules do not fight. The rule is simply:
mute means mute now, and a *new* alarm overrides it.

### Failure and teardown

- A failed `wpctl` call warns and plays no cue, so silence means the command did not land and the
  cue is a real success signal rather than decoration. `mute` is the one exception: its cue plays
  first by design, so a failed `set-mute` has already beeped. It warns, and `user_muted` is rolled
  back so the satellite's state still matches the sink.
- Connection teardown reapplies `user_muted` **if a hold is outstanding**, so a hub that dies
  mid-alarm leaves the speaker audible rather than silently muted, and the next connection settles
  the state. With no hold there is nothing to undo and no write is made.
- With `--volume-sink` absent, every action warns and does nothing.

### Cue

New `sounds/volume.wav`: a short tone distinct from `awake.wav` and `done.wav`, 22 050 Hz mono
S16LE, embedded with `include_bytes!` and decoded at startup by the existing `decode_wav_pcm`,
which already asserts that format.

It plays through the playback pump like every other cue, so it can never race a reply for the
exclusive device. Two consequences inherited from existing behaviour: cues are dropped while a
stream is active, so a command spoken during a reply gets no beep; and the cue rides the `TTS`
sink, so it is audible at the calibrated voice level regardless of how low music is.

One tone covers all four user actions. Separate rising and falling tones for up and down were
considered and dropped — the level change itself already tells you the direction.

## Settings

`McpChannelVoice/appsettings.json` alone. These are generic tunables: no secret, no
per-deployment value, so nothing goes into `DockerCompose/.env` or the compose environment block.

```json
"Commands": {
  "Enabled": true,
  "Phrases": {
    "LocalVolumeUp":   ["sube el volumen local", "sube el altavoz"],
    "LocalVolumeDown": ["baja el volumen local", "baja el altavoz"],
    "LocalMute":       ["silencia el altavoz", "mute local"],
    "LocalUnmute":     ["quita el silencio local", "unmute local"]
  }
}
```

Spanish only. The STT initial prompt is already Castilian, and a single-language table keeps the
match surface small.

`Enabled: false` turns the fast-path off entirely, so every transcript goes to the agent as it
does today.

## Testing

Red-Green-Refactor throughout, per the project rules.

**Hub, unit.**

- Matcher: matches regardless of case, accents and punctuation; a command embedded in a longer
  sentence does **not** match; an unknown phrase returns nothing; `Enabled: false` matches
  nothing.
- `TranscriptDispatcher`: a matched command creates no conversation, emits no channel message,
  returns `false`, publishes `Outcome = "command"`, and writes the expected `speaker-volume` event
  through the session's `ControlWriter`; a transcript that fails the quality gate is dropped even
  when its text matches a phrase; a session with a null `ControlWriter` logs and returns `false`
  without throwing.
- `InsistentAnnouncementController`: `alert-hold` is emitted before the first round and
  `alert-release` in the `finally`, including when the loop ends by dismissal and by cancellation.
- Wire: the `speaker-volume` event shape, and `PROTOCOL_VERSION` / `ProtocolVersion` both reading
  1.8.

**Satellite, unit.**

- Each action's effect on `user_muted` and `held`.
- `alert-hold` followed by `alert-release` round-trips a muted speaker back to muted, and leaves
  an unmuted speaker unmuted.
- `alert-release` with no preceding hold is a no-op.
- A `mute` issued during a hold applies immediately, and the later `alert-release` leaves it muted.
- `mute` defers the `set-mute` call until the cue has drained, and applies it immediately when the
  cue is dropped because a stream is active.
- Connection teardown with a hold outstanding reapplies `user_muted`; teardown with no hold makes
  no write.
- A failed `set-mute` rolls `user_muted` back so the tracked state matches the sink.
- An unknown `action` is ignored rather than erroring the connection.
- `--volume-sink` absent makes every action a no-op, including the startup mute-state read.
- `sounds/volume.wav` decodes and passes the 22 050 Hz / mono / 16-bit assertion.

The `wpctl` invocation sits behind the same probe-backend pattern `music.rs` already uses for
`amixer`, so the state rules are testable without the binary.

## Out of scope

- On-device wake-free command models.
- Playback transport, room lights, and any other fast-path command.
- Music Assistant volume, which stays with the agent.
- Per-source local levels — the `TTS` and `Alert` softvols keep their provisioned calibration.
- Keeping a follow-up conversation open across a command.
