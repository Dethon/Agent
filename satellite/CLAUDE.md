# nabu-satellite

Standalone Rust crate (NOT in the .NET solution): a fully static aarch64-musl Wyoming satellite binary (~19.9 MiB) for Raspberry Pi, embedding the openWakeWord "ok nabu" pipeline (melspectrogram → embedding → classifier ONNX, run in-process via tract) and the cue WAVs. `README.md` covers build prerequisites, hardware defaults, the LED and model licenses; this file holds the invariants that must not be broken.

## Invariants

- **The satellite is the Wyoming SERVER; the hub dials in** (default `--listen 0.0.0.0:10700`). A new hub connection supersedes the previous one (abort + await), so a dead-peer TCP wedge can't hold the exclusive `plughw` mic for the ~15-min retransmission timeout. The three ONNX models are parsed + optimized ONCE at boot (`WakeModels::load`, fail-fast) and shared across connections, so re-arm after a reconnect is instant.
- **Cancellation safety**: hub/mic reads AND playback writes/drains are multi-await compound I/O, NOT `select!`-safe. They run in dedicated pump tasks (hub, mic, playback) feeding bounded mpsc channels; the main `select!` only races `recv()` futures.
- **Playback pump** — the single owner of the playback device. `audio-stop`'s drain (~0.5-2 s of buffered TTS) happens inside the pump, so wake/button/mic stay live during the reply tail. Drain completions return on an unbounded channel (bounded would AB-deadlock) carrying a generation that gates the LED Idle/Listening transition, so a stale completion can't blank a newer stream. Playback errors stay connection-fatal, the alert sink's open being the one deliberate exception (below). Cues route through the pump too (and are dropped while a stream is active), so a cue can never EBUSY-race a reply for the exclusive device.
- **Audio contract**: mic = 16 kHz mono S16LE in 1280-sample/80 ms chunks (arecord subprocess; bytes end-to-end internally, decoded to i16 only at the detector). Playback sink = FIXED 22 050 Hz mono S16LE (aplay) that ignores announced rates — hub-side TTS, the chime and the embedded cue WAVs must all be 22 050 Hz.
- **ALSA latency flags**: defaults carry `arecord … -F 20000` (20 ms periods; the alsa-utils default of buffer/4 = 125 ms delayed every mic sample on the wake and STT paths) and `aplay … --start-delay=100000 -F 50000` (start at ~100 ms queued instead of the full 500 ms buffer, which stays for underrun headroom). Keep them when overriding devices — on **both** `--snd-command` and `--alert-snd-command`. Plain-argv audio commands exec directly (no `sh -c`) so kill/supersede SIGKILLs aplay/arecord themselves; shell-shaped commands (WSL gain pipe) still go through sh.
- **Zero-lag pre-roll**: while idle, mic chunks fill a pre-roll ring (`--preroll-ms`, default 1000); a wake trigger flushes only the detection gap (3 chunks ≈ 240 ms), never the wake word itself; a button press flushes the full ring.
- **Wire format**: frames are one contiguous buffer with event `data` sent once as the `data_length` body (the hub's reader prefers the body; its writer emits the same shape) — pinned by a codec test.

## Wake Metadata & Arbitration (PROTOCOL_VERSION 1.7)

`run-pipeline` carries `{"source":"wake"|"button","wake_rms":f32,"wake_score":f32}` — rms over the pre-roll ring minus the detection gap, BEFORE trim, in i16-amplitude units matching the hub's SilenceGate. The hub may reply `pause-satellite` (arbitration loss): Streaming → audio-stop back, Idle, detector reset, NO cue, LED Idle; Idle → no-op.

**`room_rms` (1.7, optional)** — what the room sounds like with nobody talking to the satellite, from `audio/room.rs`: the minimum of 480 ms-smoothed energy over a trailing 3 s of **idle** mic audio, same units and same statistic as the hub's own noise floor. It exists because the hub cannot measure this: its first captured frame is already the turn, so a command spoken straight after the wake word leaves its `AdaptiveLevelTracker` estimating the background from the speaker's own voice (measured at 6x the true room on prod, which armed the noisy-room regime in a silent office and endpointed people mid-sentence). Smoothing before the minimum is what stops bursty TV dialog from reading as a quiet room through its 100-400 ms lulls; the wake word needs no exclusion despite sitting at the end of the window, because loud audio cannot lower a minimum. The estimator is **reset at every trigger**, so a reading only ever describes idle audio measured since the last turn ended — a satellite that has not been idle for a full window sends **no field at all** (absent, never null or zero: the hub reads a missing value as "ask my own captures", and a zero would pin its floor at silence).

The measurement is taken **before the duck engages** (ducking starts at Listening, i.e. at the trigger), which is what makes it safe on a music unit: with music playing it reports the *unducked* level, louder than the ducked music the capture then hears, so the hub's cap is inert and its adaptive gating stays armed exactly where it is needed. In a quiet room the two are the same number.

A button-triggered `run-pipeline` carries only `{"source":"button"}` with no `wake_rms`, and the hub marks a connection pause-capable only after seeing a non-null `wake_rms` — so a satellite whose first turn on a connection is a button press still gets the legacy audible `transcript` abort if that turn loses arbitration.

The hub also sends `voice-stopped` (header-only) once it has endpointed the user's speech and is about to transcribe — deliberately NOT sent for captures abandoned to arbitration or ending in silence. The satellite uses it purely as the Thinking indicator and does **not** close its capture on it; only `transcript`, the actual turn-end signal, does that.

`listening-started` (header-only, 1.6) is its counterpart: the hub sends it from
`FollowUpConversation` just before it reopens the mic for a wake-free follow-up turn, and the
satellite uses it purely to move the turn's LED phase back to Listening. The satellite cannot
infer that moment — its own capture never closed, so from its side a reply draining looks
identical whether the agent is mid-answer or finished. Ignored while Idle, and a pre-1.6 hub
simply never sends it (the ring then keeps breathing through the window).

`audio-start` carries `alert: bool` (1.5). `true` routes the stream to `--alert-snd-command`
instead of `--snd-command` — on music units a non-attenuated `alert` softvol, so a timer or alarm
rings at full scale rather than the calibrated conversational `TTS` level. The hub sets it only for
insistent announces (timers and alarms). Read defensively and defaulted to `false`, so a pre-1.5
hub, an ordinary reply and a garbage value all keep the normal sink. If the alert device cannot be
opened the pump falls back to the normal sink and warns — never connection-fatal on open, because a
quiet alarm beats a dropped connection (a player that outlives the probe and dies mid-ring is still
an ordinary fatal playback error). That covers **both** failure shapes, which is why the fallback is
not merely an `Err` branch: a missing player binary fails `spawn()`, but the realistic case — an
undefined `pcm.alert` — spawns fine and dies on the device open, invisible until writes EPIPE well
into the ring. So an alert sink gets a 50 ms `try_wait` liveness probe before a stream is committed
to it. **Don't "optimize away" that sleep**: it is on the alert path only, never replies or cues, and
without it a misconfigured alert device drops the hub connection for the whole duration of the alarm
(the insistent loop re-enqueues on every gap).

## LED

The state machine publishes `LedState` (Idle/Listening/Thinking/Speaking) on a tokio watch channel; a per-connection render task owns the backend — the reSpeaker XVF3800's 12-LED WS2812 ring, driven over USB vendor control transfers (`bmRequestType` 0x40/0xC0, `bRequest` 0x00, `wValue` = command id, `wIndex` = resource 0x14, LE payload; every write is status-read like the vendor's `xvf_host`). Command ids: `LED_EFFECT` 0x0c, `LED_BRIGHTNESS` 0x0d, `LED_SPEED` 0x0f, `LED_COLOR` 0x10, `LED_DOA_COLOR` 0x11.

- **Looks**: Idle → effect 0 (dark), Listening → effect 4 (DoA, blue ring + green pointer), Thinking → breathing blue (effect 1), Speaking → solid blue (effect 3). Colour is always written BEFORE effect so the old colour can't flash in the new mode.
- **Phase mapping** (`handle_hub_event`): `run-pipeline` (wake or button) → Listening; `voice-stopped` → Thinking (capture stays open); `listening-started` → Listening; `audio-start` → Speaking; `transcript` → Idle. Idle after a reply therefore means actual-playback-complete. A 120 s Thinking fallback mirrors the hub reply timeout in case `voice-stopped` fired but no reply/transcript ever arrives.
- **A stream draining mid-turn returns the ring to the turn's phase, not to a fixed state.** `run_connection` carries `phase` (Listening/Thinking) alongside `mode`, and `apply_drain_done` publishes it whenever `mode` is still `Streaming` — Idle otherwise. This is what makes the seams of a segmented answer (say something → call a tool → say the rest) read as Thinking: they are one hub turn, so no `transcript` arrives between them, and hard-coding Listening there put the ring on the DoA look, which with no sound in the room renders as good as dark. Listening is still correct **before** `voice-stopped` — an announcement draining while a fresh capture is open — which is the case that mapping was originally added for.
- Enabled by default when 2886:001a is on USB (`--no-led` opts out); an absent device or USB subsystem is silent, a failed open warns. **The service needs write access to the USB node** — provisioning's `99-nabu-usb-audio.rules` sets `MODE="0660", GROUP="plugdev"` and the unit lists `plugdev`; without it the ring stays dark. `main.rs` blanks the ring at startup and on SIGTERM/SIGINT, because the ring's power-on default is lit and the render task only exists while a hub is connected.

## Music ducking

`music.rs` rides the same `LedState` watch channel as the ring (`--music-mixer`, an ALSA softvol
set with `amixer`; absent = feature off). **Every in-turn phase ducks — Listening, Thinking and
Speaking — and only Idle restores**, because a turn is one continuous thing from the user's side:
the wait between their sentence and the answer is an LLM round trip plus any tool calls, routinely
far longer than the restore grace, and restoring across it brought the music up for a few seconds
under a user waiting for a reply, then dropped it again the instant the reply spoke. Ducking
through Thinking is therefore deliberate — don't "fix" it back to restore-on-Thinking.

The satellite reaches Idle only when the hub ends the turn (`transcript`, or `pause-satellite` on
arbitration loss); a stream draining mid-turn returns to the turn's `phase`, so the seams of a
segmented answer never touch Idle. Two guards survive on top of that:

- **Restore grace** (`--music-restore-grace-ms`, default 3000) debounces the un-duck on Idle, so a
  turn that ends and immediately restarts doesn't flap.
- **Per-state duck caps** (`max_duck`) force-restore a ducked state the hub never ends: 30 s for
  Listening (a mic window left open), 120 s for Thinking — the hub's own `FollowUp.ReplyTimeoutMs`,
  the same deadline `led.rs` blanks the ring on, so a wedged turn gets its light and its music back
  together. Speaking is **uncapped**: it is bounded by drain-completion (and by connection teardown
  via `DuckGuard`), and a cap there flapped the music up ~0.5 s before a ~30 s reply finished.

## Local speaker volume

`volume.rs` drives the satellite's MASTER output level on the hub's `speaker-volume` event
(protocol 1.8, actions `up`/`down`/`mute`/`unmute`/`alert-hold`/`alert-release`), stepping by
`--volume-step` points (default 10). Master is deliberately not one of the `Music`/`TTS`/`Alert`
softvols, which carry calibration and, in `Music`'s case, are rewritten by the ducker on every
turn. Master and softvol multiply, so ducking is untouched by this feature.

**Two backends, one master.** They are the same knob — the last thing every source passes
through — reached with whatever tool the unit has:

- **Music units**: `--volume-sink` names the PipeWire sink, driven with `wpctl`. Wireplumber
  persists its level and mute, so nothing is written to disk here and a level survives a restart.
- **Voice-only units**: `--volume-mixer` (plus `--volume-card`) names an ALSA softvol, driven with
  `amixer` — the same tool and shape as the music ducker. PipeWire is installed only on music
  units, so provisioning writes a software master into `/etc/asound.conf` instead and points
  `--snd-command` at it. Software is not a shortcut: an amp HAT like the MiniAmp has no hardware
  volume control at all, so on this hardware every level is software anyway.

**`--volume-step` is not the same size on both.** It is 10 points of whatever scale the tool
moves. On PipeWire that is 10 % of a linear volume, roughly 1-2 dB near the top. On the ALSA
softvol it is 10 % of the raw 0-255 range against a taper that is linear in dB from −51.0 to 0.0,
so about 5.1 dB — a much bigger jump per command. Stepping down also bottoms out at −51 dB rather
than at silence; only `mute` truly silences a voice-only unit.

A softvol provides one element, so the voice-only master is a pair: `Nabu Volume` (level) and
`Nabu Switch` (a resolution-2 softvol, which IS a mute switch). ALSA's simple mixer merges a
`<base> Volume` element with a `<base> Switch` one, so both are the single control `Nabu` and
`amixer sset Nabu 10%+` / `sset Nabu mute` drive them. `amixer` clamps a relative step to the
control's own range, which is what `-l 1.0` does for `wpctl`. Unlike wireplumber, nothing persists
this across a reboot: the control does not exist when `alsa-restore` runs, so softvol recreates it
at maximum on the satellite's first playback.

With neither flag the whole thing is a no-op with a warning, mirroring `music_mixer: None`.
Passing BOTH is rejected by `Config::parse`: they name two tools for the same master, so whichever
one lost would leave the satellite beeping its confirmation at a level nobody hears — exactly the
failure a voice-only unit had before it had a master at all.

Everything below is backend-independent — the gate, the tracked mute and the alert hold sit above
`Backend` and behave identically on both.

An internal `tokio::sync::Mutex` gate serializes `step`, `alert_hold`, `alert_release` and
`set_user_mute` end-to-end, across their own awaited mixer call. It was added because an alert
hold and a queued mute confirmation each read the tracked state, change it, and await a mixer
call — two separate windows a concurrent call could otherwise land inside, leaving `user_muted()`
disagreeing with whichever call's process actually finished last. The gate makes such calls run
one fully after the other, so the last one to finish decides both consistently.

**`user_muted` and `alert_held` both live on `VolumeControl`, which is process-scoped.** A hub
reconnect cannot forget the user's mute, and every decision reads the two together — that is the
whole point of keeping them in one place. `user_muted` is seeded once at boot by reading the
master (`wpctl get-volume`, which prints `[MUTED]`; `amixer sget`, which prints `[on]`/`[off]`),
so the mixer's restored state and ours agree. A failed or unparsable read leaves it unmuted, the
safe direction: the speaker is audible and one spoken command fixes it.

**An alarm must ring on a muted speaker.** `alert-hold` marks the hold and unmutes the sink
WITHOUT clearing `user_muted`, so the release has something to put back. It is idempotent: the hub
re-sends it at the top of every ring round, and a repeat while the hold already stands writes
nothing. A hold whose unmute call fails un-marks itself, so the next round's re-assert retries it.
There is no counter on this side — the hub counts overlapping alerts and sends `alert-release`
only when the last one covering this satellite ends.

**A mute that arrives during a ring is deferred, not applied.** `set_user_mute(true)` under an
outstanding hold records the intent and skips the sink write; `alert-release` writes it. Without
that, "silencia el altavoz" said a second before a timer fired silenced the timer: the mute
confirmation is queued behind its cue, so it landed ~300 ms after the hold had already unmuted.
An unmute always lands at once — it cannot silence anything, and it is what the user just asked
for.

`HoldGuard` covers a hub that dies mid-alarm. On connection teardown it ends the hold and fires a
detached mixer call putting the master back to `user_muted` (Drop cannot await, same shape as
`music.rs`'s `DuckGuard`). It runs both ways: the speaker is left audible if the user never muted,
and muted if their mute was still deferred by the hold.

**The mute cue must not be silenced by its own mute**: `mute` plays the cue via
`PlaybackHandle::cue_then` and applies the mute on the acknowledgement, which the pump sends after
`play_cue` drains — and also when it drops the cue for an active stream, so the mute still lands.
The wait is a detached task, never the `select!` loop.

## Hardware: reSpeaker XVF3800 + MiniAmp (deployed fran-office unit)

- **Format**: 16 kHz-native S16LE both directions, 2ch (both capture channels carry the same processed signal, so `plughw` stereo→mono averaging is fine). Defaults target this hardware: provisioning auto-detects the USB capture card by NAME (`arecord -l`) and addresses it as `plughw:CARD=<name>,DEV=0`; capture needs no resampling, and `plughw` resamples only the 22 050 Hz playback to a rate the speaker lists (44100/48000 on the MiniAmp; a mic-card speaker output lists its own set). Override path for the ReSpeaker 2-Mic HAT: `plughw:CARD=seeed2micvoicec,DEV=0` on both audio commands plus `--button-gpio 17`.
  - Don't redo: `snd_usb_audio index=0` pinning collided with the Pi's built-in vc4-hdmi + headphone cards occupying slots 0-2 and failed the USB probe with -16 (no card created); an `/etc/asound.conf` 48 kHz plug device is impossible because capture is 16 kHz-ONLY ("no configurations available").
- **Firmware is load-bearing and upgradable in place.** Read it with `sudo LD_LIBRARY_PATH=. ./xvf_host VERSION` from `/opt/xvf3800-host/` (or `lsusb -v -d 2886:001a | grep bcdDevice`), compare against `respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY` → `xmos_firmwares/usb` (plain files, no tags/releases), and flash over the live USB link — the device exposes a runtime DFU interface, so no re-cabling: `sudo apt-get install -y dfu-util && sudo dfu-util -R -e -a 1 -D <fw>.bin`, with `nabu-satellite` and any feeder stopped first. Alt 1 is the upgrade partition, alt 0 the factory fallback (upstream also ships `xmos_firmwares/recover/4mb_all_ff.bin`). **Take the plain 2-channel build ONLY** — `_48k` and `6chl` break the 16 kHz/2ch contract.
- **Capture-clock quirk, fixed in 2.0.10.** On firmware ≤2.0.6 the capture engine only ran while its OWN playback stream was active (both UAC endpoints synchronous off one internal clock): capture-alone opened EIO, and a live capture died the instant playback stopped — hence provisioning's `nabu-micclock.service`, an endless zero-stream keeping capture clocked 24/7. The deployed unit runs **2.0.10** (flashed from 2.0.6 on 2026-07-27; mixer levels, every `xvf_host -d` parameter and the capture format survived unchanged) and the feeder is gone, verified on cold boot, after idle, under a live capture, and across a reboot with no feeder installed. The probe + feeder stay in provisioning for other UAC hardware, and the verdict is **sticky across re-provisions** — the probe is only valid on a COLD engine (once the feeder has run, the engine stays clocked for minutes, so a warm re-probe wrongly tears a still-needed feeder down). Retiring a feeder that a firmware upgrade made obsolete therefore means `sudo rm /etc/systemd/system/nabu-micclock.service` first, same as a hardware swap.
- **Speaker output** goes to the HiFiBerry MiniAmp (`dtoverlay=hifiberry-dac`, card `sndrpihifiberry`) via PipeWire; WirePlumber rules exclude the reSpeaker card and disable the unused HDMI/jack cards so the HAT is deterministically the only/default sink.
- **AEC: NEGATIVE result, don't redo without new hardware** (2026-07-16; re-checked at the 2.0.10 flash on 2026-07-27 — no 2.0.7-2.0.10 changelog touches AEC and the `xvf_host -d` dump was identical apart from `VERSION`/`BLD_REPO_HASH` and live values). With playback on the MiniAmp the XVF3800's echo cancellation stays inert even when fed a clean far-end reference: a PipeWire loopback (virtual `speaker_frontend` sink → undelayed copy to a standalone `api.alsa.pcm.sink` on the Array + a filter-chain delay stage 20-120 ms → MiniAmp, all in one `pipewire -c` client so targets exist at load) measured clip-free and level-balanced at the DSP taps (`xvf_host` mux cat 4/12 = far-end pre/post gain, cat 3 = mic into SHF, cat 7 + `AEC_ASROUTONOFF 0` = linear residual), yet linear ERLE stayed 0.0 dB across the whole delay sweep while `AEC_AECCONVERGED=1`. Vendor guidance assumes the speaker hangs off the XVF3800's OWN 3.5 mm out (`AUDIO_MGR_SYS_DELAY` trims only ±64..256 samples); a fully external DAC is outside the firmware's echo model. Real AEC needs an analog amp on the XVF3800 jack, or new firmware.
  - Gotchas worth keeping: the Array's UAC `PCM` mixer ships at −23/−20 dB as deliberate headroom for the ×8 `AUDIO_MGR_REF_GAIN` — 0 dB hard-clips the reference (now stored at −13/−20 dB via alsactl). A filter-chain sink's monitor taps the POST-graph signal. `xvf_host` + libs live at `/opt/xvf3800-host/`.

## Build & Deploy

`scripts/build-release.sh` cross-compiles via cargo-zigbuild + zig (the `zigcc-fp16-shim.sh` CC shim rewrites tract-linalg's `+fp16` -march feature to zig's `+fullfp16`) — **never run bare `cargo zigbuild` for releases**. `.cargo/config.toml` pins `-C target-cpu=cortex-a53 -C target-feature=-aes,-sha2` for the musl target (the Pi's silicon lacks the crypto extensions LLVM's cortex-a53 def would enable).

Repo-root `scripts/provision-satellite-rs.sh <user@host> [mic-device]` installs the binary plus the templated `deploy/nabu-satellite.service` unit (only dependency: `alsa-utils`; the unit pins the `performance` governor and `Nice=-10`) and, without an explicit mic device, auto-detects the USB card by name plus a USB-autosuspend-off udev rule keyed on vendor:product.

- **I2S DAC/amp HATs** (MiniAmp) declare `DAC_OVERLAY=hifiberry-dac` — I2S is electrically undiscoverable, so the script writes the dtoverlay to config.txt, reboots, waits for the card and continues (one-shot on a fresh Pi).
- **Music units** carry **three** per-source ALSA softvols on the speaker card under a master (the PipeWire sink) held at **100 %** — the MiniAmp has no hardware volume, so every level is software and all calibration lives in the source knobs. `Music` is what the satellite ducks for the whole turn (see **Music ducking** below); `TTS` (`TTS_VOLUME`%, default 65) carries replies + cues, so agent-voice loudness is calibrated independently of music — the volume knob for amp HATs that have none; `Alert` (`ALERT_VOLUME`%, default 100) carries only hub-marked timer/alarm streams via `--alert-snd-command`, so a ring is not capped by the conversational level. All three are re-asserted per provision, and both env vars are validated locally before the build. Tune live with `amixer -c <card> sset TTS <pct>%` / `sset Alert <pct>%` + `sudo alsactl store`. **The master used to sit at 0.8**, so re-provisioning an already-calibrated unit makes music *and* agent speech louder, not just alerts; the `TTS` default dropped 75 → 65 (−5.1 dB on the −51 dB taper) to absorb that on the voice side, but an explicit `TTS_VOLUME` bypasses the default — lower that value too.
- **Voice-only units** carry **one** softvol instead: the master the spoken volume commands drive (`pcm.speaker` → `Nabu Volume` + `Nabu Switch` on the output card, see **Local speaker volume**). There is no PipeWire to hold a master and no per-source calibration to keep, so `--snd-command` plays through it and alerts follow (`--alert-snd-command` defaults to `--snd-command`). Provisioning materializes the control with 1 s of silence and re-asserts it to **100 % unmuted** every run, so a re-provision restores factory loudness and can never hand back a silent speaker. **That open is a gate, not a nicety**: it is the only chance to learn whether the chain works on this hardware, and if it fails provisioning deletes the file, reverts `--snd-command` to the raw device and installs NO volume flags. A unit whose `--snd-command` will not open has no audio at all — playback errors are connection-fatal, so every cue and reply would drop the hub connection under `Restart=always` — which is far worse than the working speaker it had before; losing the volume knob is not. `/etc/asound.conf` is now **rewritten** rather than deleted on a music → voice-only downgrade; the two failure paths (no derivable card name, chain will not open) are the ones that still delete it, and both land on the identical no-flags outcome. The control name lives in ONE shell variable that feeds the asound.conf elements, the `amixer` calls and the unit's `--volume-mixer` argument, so it cannot drift.
- **Wake sensitivity** comes from `THRESHOLD` (default 0.7) and `WAKE_WINDOW` (default 2), validated locally before the build and applied to **both** unit paths (the voice-only `ExecStart` and the music drop-in that overrides it). Lowering either makes wake easier but noisier — window 1 at a low threshold is what let music itself trigger the wake word on the office satellite, so retune one knob at a time. Both substitutions rewrite the flag's *argument* rather than matching a literal default, so moving a default in the unit template can't silently render provisioning's `sed` inert.
  - `WAKE_WINDOW` replaced `TRIGGER_LEVEL` (still read as an alias, both here and as `--trigger-level` on the binary — `Config::parse` errors on unknown arguments, so a stale unit file carrying the old flag would otherwise fail to start and loop under `Restart=always`). It is no longer a count of *consecutive* frames over the threshold: it is how many 80 ms classifier scores are **averaged** before the mean is compared against `THRESHOLD`. The old counter reset to zero on any single frame below threshold, so the jittery score trace produced by background TV or music threw the utterance away and the satellite would not wake even when the phrase was clearly spoken. Window *n* is therefore strictly more permissive than the old level *n* at the same threshold, at identical latency — the old rule needed every frame at or above it, the mean only needs them to average it.
  - **Above 3 the added latency exceeds `--wake-preroll-ms`** (240 ms, sized for the ~181 ms measured detection latency): each step adds one 80 ms frame before the window fills, so raise the pre-roll gap flush in the same change or the start of the user's speech is clipped from what reaches the hub.
- **qemu smoke tests need `--no-wake`** (qemu's fp16 hwcaps activate tract f16 kernels that crash under emulation; a real A53 selects f32). On-device E2E validation is still open, blocked on hardware — it should also read the `RUST_LOG=debug` per-chunk "wake inference" timing line.

## Running on WSL

Repo-root `scripts/wsl-satellite.sh` (WSLg PulseAudio) and `scripts/wsl-satellite-winaudio.sh`
(Windows-native ffmpeg/ffplay, because WSLg's RDP audio bridge audibly degrades playback). Both
scripts' header comments carry the env knobs and the audio-latency caveats; `README.md` has the
manual invocation.
