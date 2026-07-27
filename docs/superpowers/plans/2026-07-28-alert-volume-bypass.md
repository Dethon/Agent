# Alert Volume Bypass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make timers and alarms ring at full scale by routing them past the per-satellite voice-volume softvol, instead of sharing the attenuated path used by ordinary agent speech.

**Architecture:** The hub marks an insistent-announce stream with a new `alert` flag on the Wyoming `audio-start` event (protocol 1.4 → 1.5). The satellite's playback pump opens a *different* ALSA sink for a marked stream — a new non-attenuated `pcm.alert` softvol written by provisioning — while ordinary replies and cues keep using the existing `tts` sink. Nothing is mutated at ring time: the bypass is pure routing, so a crash cannot strand a volume control at the wrong level.

**Tech Stack:** Rust (tokio, pico-args, serde_json) for the satellite; .NET 10 / C# for the voice hub (`McpChannelVoice`); xUnit + Shouldly + Moq for hub tests, inline `#[cfg(test)]` modules for satellite tests; Bash for provisioning.

**Spec:** `docs/superpowers/specs/2026-07-28-alert-volume-bypass-design.md`

## Global Constraints

- **Branch:** commit on the currently checked-out branch. Do **NOT** switch branches or create new ones.
- **TDD is mandatory:** write the failing test, run it, *see it fail*, then implement. Never write implementation before a red test.
- **`.cs` files have NO trailing newline** (`.editorconfig` sets `insert_final_newline = false` for `*.{cs,csx,vb,vbx}`). `.rs`, `.md` and `.sh` files DO end with a newline.
- **The pre-commit hook re-stages whole files** (`.githooks/pre-commit` runs `dotnet format` over staged `.cs` files and `git add`s them entirely). Partial/hunk staging does not survive a commit — make the working tree match the commit you want.
- **C# style:** file-scoped namespaces, `record` for DTOs, no XML doc comments, comments explain *why* not *what*, prefer LINQ over loops.
- **Rust commands must run from inside `satellite/`** — `satellite/rust-toolchain.toml` pins `1.97.1`, and rustup resolves it from the current directory. Running `cargo` from the repo root picks up the 1.91 default and fails the `rust-version = "1.97"` floor.
- **Satellite test baseline is green:** 75 lib tests + 2 `spike_wake` integration tests pass before any change.
- **Protocol version moves on both sides together:** `satellite/src/wyoming/event.rs` `PROTOCOL_VERSION` and `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs` `ProtocolVersion`. Both go to `"1.5"` (Tasks 3 and 5). Neither side validates the value; drift only costs a misleading wire trace, but they are documented as one number.
- **The 22 050 Hz playback contract is unchanged.** Every hub-emitted stream, including alerts, stays 22 050 Hz mono S16LE.
- **Do not reuse `AnnouncePriority.High` as the alert marker.** Approval prompts and `WyomingSatelliteHost`'s own wake announcement also use `High` and must not ring at alert level.

**Commands:**

```bash
# Rust (from inside satellite/)
cd /home/dethon/repos/agent/satellite && cargo test

# C#
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~<TestClass>"

# Shell syntax check
bash -n /home/dethon/repos/agent/scripts/provision-satellite-rs.sh
```

---

## File Structure

**Satellite (Rust)**

| File | Change | Responsibility |
|---|---|---|
| `satellite/src/config.rs` | Modify | New `alert_snd_command` field + `--alert-snd-command` flag, defaulting to `snd_command` |
| `satellite/src/audio/playback.rs` | Modify | Pump holds two sink commands; `Start` carries `alert`; `open_sink` picks and falls back |
| `satellite/src/satellite/state_machine.rs` | Modify | Reads `alert` off `audio-start`; passes both commands to `spawn_pump` |
| `satellite/src/wyoming/event.rs` | Modify | `PROTOCOL_VERSION` → `1.5`; `data_obj` loses `#[allow(dead_code)]` |
| `satellite/CLAUDE.md` | Modify | Protocol version, alert-route invariant, three-softvol description |

**Hub (C#)**

| File | Change | Responsibility |
|---|---|---|
| `McpChannelVoice/Services/SatelliteSession.cs` | Modify | `PlaybackJob.Alert`; `onAudioStart` carries the flag |
| `McpChannelVoice/Services/WyomingSatelliteHost.cs` | Modify | `BuildAudioStart(format, alert)` pure builder + wiring |
| `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs` | Modify | `ProtocolVersion` → `1.5` |
| `McpChannelVoice/Services/InsistentAnnouncementController.cs` | Modify | Sets `Alert: true` — the only producer |
| `McpChannelVoice/Services/AlarmTone.cs` | Modify | `Amplitude` 0.5 → 0.9 |
| `Tests/Unit/McpChannelVoice/WyomingSatelliteHostAudioStartTests.cs` | Create | Pure-builder tests for the audio-start frame |
| `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs` | Modify | Flag reaches `onAudioStart` |
| `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs` | Modify | Timer and alarm jobs are alerts |
| `Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs` | Modify | A plain announce is not an alert |
| `Tests/Unit/McpChannelVoice/AlarmToneTests.cs` | Modify | Pins the new amplitude |

**Provisioning (Bash)**

| File | Change | Responsibility |
|---|---|---|
| `scripts/provision-satellite-rs.sh` | Modify | Latency flags (Task 8); `pcm.alert` + `ALERT_VOLUME` + master 1.0 + `--alert-snd-command` (Task 9) |
| `CLAUDE.md` | Modify | Voice Satellite Architecture section (Task 10) |

---

## Task 1: Satellite `--alert-snd-command` flag

**Files:**
- Modify: `satellite/src/config.rs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `Config.alert_snd_command: String` — always non-empty; equals `Config.snd_command` unless `--alert-snd-command` is passed. Tasks 2 and 3 read it.

**Context:** `Config` is a plain struct with a hand-written `Default` impl and a `parse(pico_args::Arguments)` function. The doc comment above `from_args` lists every flag and must stay accurate. The default `snd_command` string carries a long explanatory comment about the ALSA latency flags — hoist it to a `const` so both fields share one source of truth rather than duplicating the string.

- [ ] **Step 1: Write the failing tests**

Add to the `mod tests` block at the bottom of `satellite/src/config.rs`:

```rust
    // A unit that overrides only --snd-command (all of provisioning does) must not end up with
    // alerts pointed at the compiled-in default device. Alerts follow the normal sink unless
    // explicitly given their own, so a voice-only satellite needs no new provisioning at all.
    #[test]
    fn alert_snd_command_defaults_to_the_snd_command() {
        let c = Config::default();
        assert_eq!(c.alert_snd_command, c.snd_command);

        let c = Config::parse(args(&["--snd-command", "aplay -D tts -r 22050"])).unwrap();
        assert_eq!(c.snd_command, "aplay -D tts -r 22050");
        assert_eq!(c.alert_snd_command, "aplay -D tts -r 22050");
    }

    #[test]
    fn alert_snd_command_flag_overrides_only_the_alert_sink() {
        let c = Config::parse(args(&[
            "--snd-command", "aplay -D tts -r 22050",
            "--alert-snd-command", "aplay -D alert -r 22050",
        ]))
        .unwrap();
        assert_eq!(c.snd_command, "aplay -D tts -r 22050");
        assert_eq!(c.alert_snd_command, "aplay -D alert -r 22050");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dethon/repos/agent/satellite && cargo test alert_snd_command
```

Expected: FAIL — compile error, `no field 'alert_snd_command' on type 'Config'`.

- [ ] **Step 3: Add the field, the default, and the flag**

In `satellite/src/config.rs`, above `impl Default for Config`, hoist the default playback command to a const, moving the existing explanatory comment onto it verbatim:

```rust
// --start-delay=100000 (µs): aplay's default start threshold is the FULL 500 ms buffer, so a
// streamed reply isn't audible until 500 ms of audio has been synthesized+delivered; start at
// 100 ms queued instead (buffer stays 500 ms for underrun headroom). -F 50000 reads stdin in
// 50 ms periods so the first write into the ALSA buffer happens sooner.
const DEFAULT_SND_COMMAND: &str =
    "aplay -D plughw:CARD=sndrpihifiberry,DEV=0 -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000";
```

In the `Config` struct, add the field directly after `snd_command`:

```rust
    pub snd_command: String,
    // Sink for hub-marked ALERT streams (timers/alarms). Defaults to snd_command, so a unit
    // without a dedicated non-attenuated route behaves exactly as before; music units point it
    // at the `alert` softvol so an alarm bypasses the calibrated `TTS` voice level.
    pub alert_snd_command: String,
```

In `impl Default for Config`, replace the inline `snd_command:` string (and its comment block, now on the const) with:

```rust
            snd_command: DEFAULT_SND_COMMAND.into(),
            alert_snd_command: DEFAULT_SND_COMMAND.into(),
```

In `parse`, immediately after the existing `--snd-command` line, add:

```rust
        // Read AFTER --snd-command so the fallback sees the final value: a unit that overrides
        // only the normal sink gets its alerts on that same sink, not on the compiled-in default.
        c.alert_snd_command = pa
            .opt_value_from_str::<_, String>("--alert-snd-command")?
            .unwrap_or_else(|| c.snd_command.clone());
```

In the `from_args` doc comment, add `--alert-snd-command` next to `--snd-command`:

```rust
    /// Flags: --listen --mic-command --snd-command --alert-snd-command --threshold --trigger-level --no-wake
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dethon/repos/agent/satellite && cargo test
```

Expected: PASS — 77 lib tests (75 baseline + 2 new), 0 failed. The existing `defaults_are_sane` and `audio::mod::tests::default_audio_commands_bypass_the_shell` must still pass, proving the const hoist preserved the string exactly.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add satellite/src/config.rs
git commit -m "feat(satellite): --alert-snd-command flag, defaulting to --snd-command

Alert streams (timers/alarms) get their own sink command so they can bypass
the calibrated TTS softvol. Defaults to the normal sink, so a voice-only unit
is unaffected and needs no provisioning change."
```

---

## Task 2: Playback pump routes alert streams to the alert sink

**Files:**
- Modify: `satellite/src/audio/playback.rs`
- Modify: `satellite/src/satellite/state_machine.rs` (call sites only — behaviour unchanged here)

**Interfaces:**
- Consumes: `Config.alert_snd_command` from Task 1.
- Produces:
  - `spawn_pump(snd_command: &str, alert_snd_command: &str) -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, JoinHandle<()>)`
  - `PlaybackHandle::start(&mut self, alert: bool) -> anyhow::Result<()>`
  - `PlaybackCmd::Start { generation: u64, alert: bool }`
  - Task 3 calls `playback.start(alert)` with a value read off the wire.

**Context:** The pump is the single owner of the playback device. It opens a fresh `PlaybackSink` per stream at `Start` and closes it at `Stop`, so switching PCM between streams cannot overlap or EBUSY-race. Playback errors are connection-fatal by design — which is exactly why the alert sink needs a non-fatal fallback: an absent alert device must make the alarm quiet, not drop the hub connection. Cues keep using the normal command: they are voice-class earcons, not alerts.

- [ ] **Step 1: Write the failing tests**

Add to the `mod tests` block at the bottom of `satellite/src/audio/playback.rs`:

```rust
    // Unique temp paths per test: the suite runs in-process in parallel, so a shared name would
    // let two tests append to the same file.
    fn sink_paths(tag: &str) -> (std::path::PathBuf, std::path::PathBuf) {
        let dir = std::env::temp_dir();
        let pid = std::process::id();
        let normal = dir.join(format!("nabu-{tag}-normal-{pid}.raw"));
        let alert = dir.join(format!("nabu-{tag}-alert-{pid}.raw"));
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
        (normal, alert)
    }

    fn cleanup(paths: &[&std::path::Path]) {
        for p in paths {
            let _ = std::fs::remove_file(p);
        }
    }

    // THE routing guarantee: an alert stream (timer/alarm) must open the alert sink and must not
    // touch the normal one. `cat >> <file>` creates its file on open, so "the normal file does
    // not exist" proves the normal player was never even spawned.
    #[tokio::test]
    async fn pump_routes_an_alert_stream_to_the_alert_sink() {
        let (normal, alert) = sink_paths("route-alert");
        let (mut handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert!(d.result.is_ok());

        assert_eq!(std::fs::metadata(&alert).map(|m| m.len()).unwrap_or(0), 64);
        assert!(!normal.exists(), "an alert stream must not open the normal sink");
        cleanup(&[&normal, &alert]);
    }

    #[tokio::test]
    async fn pump_routes_a_normal_stream_to_the_normal_sink() {
        let (normal, alert) = sink_paths("route-normal");
        let (mut handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(false).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 64);
        assert!(!alert.exists(), "a reply must not open the alert sink");
        cleanup(&[&normal, &alert]);
    }

    // Cues are voice-class earcons (awake/done/chime), never alerts — even immediately after an
    // alert stream has set the pump's most recent generation.
    #[tokio::test]
    async fn cues_always_play_on_the_normal_sink() {
        let (normal, alert) = sink_paths("route-cue");
        let (mut handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        done_rx.recv().await.unwrap(); // stream over -> cues are no longer dropped

        handle.cue(vec![1u8; 32]);
        for _ in 0..200 {
            if std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0) == 32 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 32);
        cleanup(&[&normal, &alert]);
    }

    // An absent/misconfigured alert device must make the alarm QUIET, not drop the hub connection.
    // A nonexistent binary is plain argv, so build_command execs it directly and spawn() fails
    // with ENOENT — the real "device can't be opened" shape.
    #[tokio::test]
    async fn alert_sink_open_failure_falls_back_to_the_normal_sink_non_fatally() {
        let (normal, _) = sink_paths("route-fallback");
        let (mut handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            "/nonexistent/aplay -D alert",
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();

        assert!(d.result.is_ok(), "an unavailable alert sink must not be fatal: {:?}", d.result);
        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 64);
        cleanup(&[&normal]);
    }
```

Update the four existing tests in this file that call the changed signatures:

```rust
// pump_reports_drain_done_with_stream_generation
let (mut handle, mut done_rx, _task) = spawn_pump("cat >/dev/null", "cat >/dev/null");
handle.start(false).await.unwrap();

// pump_serializes_cue_and_stream_on_an_exclusive_device
let (mut handle, mut done_rx, _task) = spawn_pump(&snd, &snd);
handle.cue(vec![0u8; 8820]);
handle.start(false).await.unwrap();

// pump_playback_error_is_reported_fatally
let (mut handle, mut done_rx, _task) = spawn_pump("exit 1", "exit 1");
handle.start(false).await.unwrap();

// idle_pump_leaves_the_device_untouched
let (_handle, _done_rx, task) = spawn_pump(&snd, &snd);
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dethon/repos/agent/satellite && cargo test --lib audio::playback
```

Expected: FAIL — compile error, `spawn_pump` takes 1 argument but 2 were supplied / `start` takes 0 arguments but 1 was supplied.

- [ ] **Step 3: Implement per-stream sink selection**

In `satellite/src/audio/playback.rs`:

Change the `Start` variant of `PlaybackCmd`:

```rust
pub enum PlaybackCmd {
    /// Begin a stream (kills a still-open previous stream: mid-stream preempt). `alert` routes
    /// the stream to the alert sink — a non-attenuated ALSA route on music units, so a timer or
    /// alarm bypasses the calibrated voice level.
    Start { generation: u64, alert: bool },
```

Change `PlaybackHandle::start`:

```rust
    pub async fn start(&mut self, alert: bool) -> anyhow::Result<()> {
        self.generation += 1;
        self.send(PlaybackCmd::Start { generation: self.generation, alert }).await
    }
```

Change `spawn_pump` and `run_pump` to carry both commands:

```rust
pub fn spawn_pump(
    snd_command: &str,
    alert_snd_command: &str,
) -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, tokio::task::JoinHandle<()>) {
    let (cmd_tx, cmd_rx) = mpsc::channel(16);
    // Unbounded ON PURPOSE: a bounded blocking send from the pump could AB-deadlock against a
    // main loop blocked sending a command. Completions are tiny and at most one per stream.
    let (done_tx, done_rx) = mpsc::unbounded_channel();
    let task = tokio::spawn(run_pump(
        snd_command.to_string(),
        alert_snd_command.to_string(),
        cmd_rx,
        done_tx,
    ));
    (PlaybackHandle { cmd_tx, generation: 0 }, done_rx, task)
}

async fn run_pump(
    snd_command: String,
    alert_snd_command: String,
    mut cmd_rx: mpsc::Receiver<PlaybackCmd>,
    done_tx: mpsc::UnboundedSender<DrainDone>,
) {
```

In `run_pump`'s match, change the `Start` arm:

```rust
            PlaybackCmd::Start { generation: g, alert } => {
                generation = g;
                streaming = true;
                if let Some(p) = sink.take() { p.kill().await; } // mid-stream preempt
                open_sink(&snd_command, &alert_snd_command, alert).map(|p| sink = Some(p))
            }
```

Add `open_sink` beside `play_cue`:

```rust
/// Open the sink for one stream. Playback-open errors are connection-fatal by design, so an
/// alert whose dedicated device is missing falls back to the normal sink instead: an alarm that
/// rings quietly beats one that drops the hub connection. Only the normal sink failing is fatal.
/// Skips the retry when both commands are identical (the default), so a genuine device failure
/// reports once rather than twice.
fn open_sink(snd: &str, alert_snd: &str, alert: bool) -> anyhow::Result<PlaybackSink> {
    if !alert || alert_snd == snd {
        return PlaybackSink::start(snd);
    }
    PlaybackSink::start(alert_snd).or_else(|e| {
        tracing::warn!("alert sink unavailable, falling back to the normal sink: {e:#}");
        PlaybackSink::start(snd)
    })
}
```

In `satellite/src/satellite/state_machine.rs`, update the two production call sites (behaviour unchanged — Task 3 makes it read the wire):

```rust
    let (mut playback, mut playback_done, pump_task) =
        spawn_pump(&cfg.snd_command, &cfg.alert_snd_command);
```

```rust
        "audio-start" => {
            playback.start(false).await?;
            let _ = ctx.led.send(LedState::Speaking); // replies AND standalone announcements
        }
```

And the test helper in that file's `mod tests`:

```rust
    fn pump() -> (PlaybackHandle, tokio::sync::mpsc::UnboundedReceiver<DrainDone>, AbortOnDrop) {
        let (handle, done_rx, task) = spawn_pump("cat >/dev/null", "cat >/dev/null");
        (handle, done_rx, AbortOnDrop(task))
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dethon/repos/agent/satellite && cargo test
```

Expected: PASS — 81 lib tests (77 + 4 new), 0 failed.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add satellite/src/audio/playback.rs satellite/src/satellite/state_machine.rs
git commit -m "feat(satellite): playback pump routes alert streams to a second sink

Start carries an alert flag and the pump holds both sink commands, so a
timer/alarm can play on a non-attenuated ALSA route while replies and cues keep
the calibrated TTS sink. An unopenable alert device falls back to the normal
sink rather than going connection-fatal."
```

---

## Task 3: Satellite reads `alert` off `audio-start` (protocol 1.5)

**Files:**
- Modify: `satellite/src/satellite/state_machine.rs`
- Modify: `satellite/src/wyoming/event.rs`

**Interfaces:**
- Consumes: `PlaybackHandle::start(alert: bool)` from Task 2.
- Produces: the satellite half of the wire contract — `audio-start` `data.alert == true` routes to the alert sink; a missing or non-boolean field routes normally.

**Context:** `handle_hub_event` dispatches on `e.event_type`. `WyomingEvent::data_obj()` already exists for exactly this purpose and carries an `#[allow(dead_code)] // no production callers yet; kept for hub-event field access` note — this is its first production caller, so drop the attribute. The field must be read defensively: the hub is a peer and this runs on the connection's event path, where a panic drops the satellite mid-alarm.

- [ ] **Step 1: Write the failing tests**

Add to the `mod tests` block in `satellite/src/satellite/state_machine.rs`:

```rust
    // Like pump(), but with distinguishable sinks so a test can prove which one a stream opened.
    fn pump_with(
        normal: &std::path::Path,
        alert: &std::path::Path,
    ) -> (PlaybackHandle, tokio::sync::mpsc::UnboundedReceiver<DrainDone>, AbortOnDrop) {
        let (handle, done_rx, task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );
        (handle, done_rx, AbortOnDrop(task))
    }

    fn frame_paths(tag: &str) -> (std::path::PathBuf, std::path::PathBuf) {
        let dir = std::env::temp_dir();
        let pid = std::process::id();
        let normal = dir.join(format!("nabu-sm-{tag}-normal-{pid}.raw"));
        let alert = dir.join(format!("nabu-sm-{tag}-alert-{pid}.raw"));
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
        (normal, alert)
    }

    // A hub-marked alert (timer/alarm) must open the non-attenuated alert sink so it rings at
    // full scale instead of the calibrated conversational voice level.
    #[tokio::test]
    async fn audio_start_marked_alert_routes_to_the_alert_sink() {
        let (normal, alert) = frame_paths("marked");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump_with(&normal, &alert);

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0,"alert":true}),
        );
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&alert).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!normal.exists(), "an alert must not reach the voice sink");
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }

    // Back-compat: a pre-1.5 hub sends audio-start with no `alert` field, and every ordinary
    // reply omits it too. Both must keep the calibrated voice sink.
    #[tokio::test]
    async fn audio_start_without_the_alert_field_routes_to_the_normal_sink() {
        let (normal, alert) = frame_paths("unmarked");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump_with(&normal, &alert);

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0}),
        );
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!alert.exists(), "a reply must not reach the alert sink");
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }

    // A non-boolean `alert` is peer-supplied garbage read on the connection's event path: it must
    // degrade to "not an alert", never panic and drop the satellite mid-turn.
    #[tokio::test]
    async fn audio_start_with_a_non_boolean_alert_routes_to_the_normal_sink() {
        let (normal, alert) = frame_paths("garbage");
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump_with(&normal, &alert);

        let start = WyomingEvent::with_data(
            "audio-start",
            json!({"rate":22050,"width":2,"channels":1,"timestamp":0,"alert":"yes"}),
        );
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let chunk = WyomingEvent { event_type: "audio-chunk".into(), data: None, payload: vec![9u8; 48] };
        handle_hub_event(chunk, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 48);
        assert!(!alert.exists());
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
    }
```

Add to the `mod tests` block in `satellite/src/wyoming/event.rs`:

```rust
    // The alert routing field on audio-start landed in 1.5; the constant is documented as ONE
    // number shared with the hub's WyomingWriter.ProtocolVersion, so it moves with the wire.
    #[test]
    fn protocol_version_is_1_5() {
        assert_eq!(PROTOCOL_VERSION, "1.5");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dethon/repos/agent/satellite && cargo test
```

Expected: FAIL — 3 routing tests fail with the alert bytes landing in the *normal* file (`assert_eq!` on `alert` length gets 0, or `!normal.exists()` fails), and `protocol_version_is_1_5` fails with `left: "1.4", right: "1.5"`.

- [ ] **Step 3: Read the flag and bump the version**

In `satellite/src/satellite/state_machine.rs`, replace the `"audio-start"` arm:

```rust
        "audio-start" => {
            // A hub-marked alert (timer/alarm) plays on the non-attenuated alert route, bypassing
            // the per-satellite voice level. Read defensively: the field is peer-supplied and a
            // pre-1.5 hub omits it entirely, and this runs on the connection's event path where a
            // panic would drop the satellite mid-turn.
            let alert = e.data_obj().get("alert").and_then(|v| v.as_bool()).unwrap_or(false);
            playback.start(alert).await?;
            let _ = ctx.led.send(LedState::Speaking); // replies AND standalone announcements
        }
```

In `satellite/src/wyoming/event.rs`:

```rust
pub const PROTOCOL_VERSION: &str = "1.5"; // matches the hub's WyomingWriter
```

and drop the now-false attribute on `data_obj`:

```rust
    /// The event's `data` as a JSON object map (cloned).
    /// Returns an empty map when `data` is `None` or not a JSON object.
    pub fn data_obj(&self) -> Map<String, Value> {
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dethon/repos/agent/satellite && cargo test
```

Expected: PASS — 85 lib tests (81 + 4 new), 0 failed, plus 2 `spike_wake` tests.

- [ ] **Step 5: Update `satellite/CLAUDE.md`**

Change the section heading:

```markdown
## Wake Metadata & Arbitration (PROTOCOL_VERSION 1.5)
```

Append this paragraph to that section:

```markdown
`audio-start` carries `alert: bool` (1.5). `true` routes the stream to `--alert-snd-command`
instead of `--snd-command` — on music units a non-attenuated `alert` softvol, so a timer or alarm
rings at full scale rather than the calibrated conversational `TTS` level. The hub sets it only for
insistent announces (timers and alarms). Read defensively and defaulted to `false`, so a pre-1.5
hub, an ordinary reply and a garbage value all keep the normal sink. If the alert device cannot be
opened the pump falls back to the normal sink and warns — never connection-fatal, because a quiet
alarm beats a dropped connection.
```

In the **ALSA latency flags** invariant bullet, extend the last sentence so the second sink is covered:

```markdown
Keep them when overriding devices — on **both** `--snd-command` and `--alert-snd-command`.
```

- [ ] **Step 6: Commit**

```bash
cd /home/dethon/repos/agent
git add satellite/src/satellite/state_machine.rs satellite/src/wyoming/event.rs satellite/CLAUDE.md
git commit -m "feat(satellite): route audio-start marked alert:true to the alert sink

Protocol 1.5. Timers and alarms now bypass the calibrated TTS softvol. The
field is read defensively and defaults to false, so a pre-1.5 hub, an ordinary
reply and a non-boolean value all keep the normal sink. data_obj gets its first
production caller and loses its dead_code allowance."
```

---

## Task 4: Hub `PlaybackJob.Alert` reaches `onAudioStart`

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs:8-17` (record), `:383` (delegate type), `:446-449` (call)
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (independent of the Rust side).
- Produces:
  - `PlaybackJob(..., long EnqueuedAt = 0, bool Alert = false)` — appended last, so no positional call site shifts.
  - `RunPlaybackLoopAsync(..., Func<AudioFormat, bool, CancellationToken, Task>? onAudioStart = null, ...)` — the `bool` is the job's `Alert`.
  - Tasks 5 and 6 consume both.

**Context:** `PlaybackJob` is a positional record whose optional members are all passed by name at every call site (`InsistentAnnouncementController.BuildJob`, `AnnouncementService`, `SendReplyTool`, `RequestApprovalTool`, `WyomingSatelliteHost`), so appending a defaulted parameter is safe. `onAudioStart` has exactly one production caller (`WyomingSatelliteHost`) and, before this task, no test coverage at all.

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`:

```csharp
    // The alert bit is what the hub puts on the wire for the satellite's sink selection, so it has
    // to survive the queue and arrive with the stream it belongs to — not with a neighbouring job.
    [Fact]
    public async Task RunPlaybackLoop_ReportsEachJobsAlertFlagOnAudioStart()
    {
        var session = MakeSession();
        var flags = new List<bool>();

        var pumpTask = session.RunPlaybackLoopAsync(
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            onAudioStart: (_, alert, _) =>
            {
                lock (flags) { flags.Add(alert); }
                return Task.CompletedTask;
            });

        var reply = new PlaybackJob(
            Label: "reply",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("reply", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var alarm = reply with { Label = "alarm", Audio = GenerateAudio("alarm", count: 1), Alert = true };

        await session.EnqueuePlaybackAsync(reply, queueMaxDepth: 8);
        await session.EnqueuePlaybackAsync(alarm, queueMaxDepth: 8);
        session.CompletePlayback();
        await pumpTask;

        flags.ShouldBe(new[] { false, true });
    }
```

`GenerateAudio` is an existing private helper in this file, and `session.CompletePlayback(); await pumpTask;` is how every other test here ends the loop — awaiting the pump matters, because an unobserved faulted playback task leaks across xUnit's parallel test classes.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests"
```

Expected: FAIL — compile error, `'PlaybackJob' has no property 'Alert'` and no overload of `RunPlaybackLoopAsync` matches the 3-argument `onAudioStart` lambda.

- [ ] **Step 3: Add the flag and plumb it through**

In `McpChannelVoice/Services/SatelliteSession.cs`, append to the `PlaybackJob` record:

```csharp
public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted,
    Func<Task>? OnDrained = null,
    Func<FirstAudioTiming, Task>? OnFirstAudio = null,
    Func<Exception, Task>? OnFailed = null,
    long EnqueuedAt = 0,
    // Timer/alarm audio. Carried to the satellite on audio-start so it plays on the
    // non-attenuated alert route instead of the calibrated per-satellite voice level.
    bool Alert = false);
```

Change the `onAudioStart` parameter type:

```csharp
        Func<AudioFormat, bool, CancellationToken, Task>? onAudioStart = null,
```

Change the call:

```csharp
                        if (onAudioStart is not null)
                        {
                            await onAudioStart(chunk.Format, job.Alert, jobCts.Token);
                        }
```

In `McpChannelVoice/Services/WyomingSatelliteHost.cs`, update the lambda so the project compiles (Task 5 replaces the body with the shared builder):

```csharp
            onAudioStart: (format, alert, sct) => client.WriteAsync(WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = format.SampleRateHz,
                ["width"] = format.SampleWidthBytes,
                ["channels"] = format.Channels,
                ["timestamp"] = 0,
                ["alert"] = alert
            }), sct),
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSession"
```

Expected: PASS — all `SatelliteSession*` test classes green, including the new test.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs
git commit -m "feat(voice): PlaybackJob.Alert flows to onAudioStart

The playback loop reports each job's alert bit alongside its audio format so the
Wyoming host can mark the stream for the satellite's alert sink."
```

---

## Task 5: Hub emits the `alert` field (protocol 1.5)

**Files:**
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs`
- Modify: `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs:12`
- Create: `Tests/Unit/McpChannelVoice/WyomingSatelliteHostAudioStartTests.cs`

**Interfaces:**
- Consumes: the `bool alert` parameter on `onAudioStart` from Task 4.
- Produces: `public static JsonObject WyomingSatelliteHost.BuildAudioStart(AudioFormat format, bool alert)`.

**Context:** `WyomingSatelliteHost` already exposes a pure static helper for the other peer-facing frame shape (`ReadWakeAnnouncement`, tested directly in `WyomingSatelliteHostWakeAnnouncementTests`). Follow that pattern: extract the frame construction to a static method and test it directly rather than trying to stand up a whole host. `AudioFormat` lives in `Domain.DTOs.Voice`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/WyomingSatelliteHostAudioStartTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// audio-start is where the satellite learns which sink a stream belongs on: an alert (timer or
// alarm) plays on the non-attenuated route, everything else on the calibrated voice level. The
// format fields must survive unchanged — the satellite's playback sink is fixed at 22050 Hz and a
// wrong rate here would be a silent pitch bug.
public class WyomingSatelliteHostAudioStartTests
{
    private static readonly AudioFormat _playback = new()
    {
        SampleRateHz = 22_050,
        SampleWidthBytes = 2,
        Channels = 1
    };

    [Fact]
    public void BuildAudioStart_AlertStream_MarksTheFrame()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: true);

        data["alert"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void BuildAudioStart_NormalStream_MarksTheFrameFalse()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: false);

        data["alert"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void BuildAudioStart_CarriesTheAudioFormatUnchanged()
    {
        var data = WyomingSatelliteHost.BuildAudioStart(_playback, alert: true);

        data["rate"]!.GetValue<int>().ShouldBe(22_050);
        data["width"]!.GetValue<int>().ShouldBe(2);
        data["channels"]!.GetValue<int>().ShouldBe(1);
        data["timestamp"]!.GetValue<int>().ShouldBe(0);
    }

    // Documented as ONE number with satellite/src/wyoming/event.rs PROTOCOL_VERSION, which has its
    // own test; the alert field on audio-start is the 1.5 change, so the two move together.
    [Fact]
    public void ProtocolVersion_MatchesTheSatellite()
    {
        WyomingWriter.ProtocolVersion.ShouldBe("1.5");
    }
}
```

That last assertion needs `using McpChannelVoice.Services.WyomingProtocol;` in the file's usings, and `ProtocolVersion` widened from `private` to `internal` in Step 3. `McpChannelVoice.csproj` already declares `<InternalsVisibleTo Include="Tests" />`, so no reflection is needed.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSatelliteHostAudioStartTests"
```

Expected: FAIL — compile error, `'WyomingSatelliteHost' does not contain a definition for 'BuildAudioStart'`.

- [ ] **Step 3: Extract the builder and bump the version**

In `McpChannelVoice/Services/WyomingSatelliteHost.cs`, add the static builder beside `ReadWakeAnnouncement`:

```csharp
    // `alert` tells the satellite to play this stream on its non-attenuated alert route, bypassing
    // the per-satellite voice level. Emitted on every stream, not only alerts, so a wire trace
    // shows the routing explicitly; a pre-1.5 satellite ignores the unknown field.
    public static JsonObject BuildAudioStart(AudioFormat format, bool alert) => new()
    {
        ["rate"] = format.SampleRateHz,
        ["width"] = format.SampleWidthBytes,
        ["channels"] = format.Channels,
        ["timestamp"] = 0,
        ["alert"] = alert
    };
```

Replace the inline `onAudioStart` lambda body with the builder:

```csharp
            onAudioStart: (format, alert, sct) => client.WriteAsync(
                WyomingEvent.Header("audio-start", BuildAudioStart(format, alert)), sct),
```

In `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs` — widened to `internal` so the test can
assert it without reflection (the project already grants `InternalsVisibleTo("Tests")`):

```csharp
    // Must match satellite/src/wyoming/event.rs PROTOCOL_VERSION. Neither side validates the value
    // today, so the only cost of drift is a misleading wire trace — but the two are documented as
    // one number (satellite/CLAUDE.md), so they move together. 1.5 added `alert` on audio-start.
    internal const string ProtocolVersion = "1.5";
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~Wyoming"
```

Expected: PASS — all `Wyoming*` test classes green.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs Tests/Unit/McpChannelVoice/WyomingSatelliteHostAudioStartTests.cs
git commit -m "feat(voice): emit the alert flag on audio-start, protocol 1.5

BuildAudioStart is a pure static builder like ReadWakeAnnouncement, so the
frame shape is testable without standing up a host."
```

---

## Task 6: Only insistent announces are alerts

**Files:**
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs` (`BuildJob`)
- Test: `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`
- Test: `Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs`

**Interfaces:**
- Consumes: `PlaybackJob.Alert` and the 3-arg `onAudioStart` from Task 4.
- Produces: nothing new — this is the policy decision that makes the feature reach only timers and alarms.

**Context:** `/api/voice/announce` routes to `InsistentAnnouncementController` **iff** the request carries `insistent`, and to `AnnouncementService` otherwise (`AnnounceEndpoint.Map`). `TimerFireService` always sets `Insistent`, and the HA alarm idiom mandates it (`HomeAssistantPrompt`: "`insistent` must be present — omitting it makes a one-shot announce, not an alarm"). So marking the controller's jobs is exactly "timers and alarms", and download notifications, approval prompts and plain announcements are untouched.

The existing test harness's `PumpPlays` helper counts writer invocations. Extend it to also capture alert flags rather than adding a second helper.

- [ ] **Step 1: Write the failing tests**

In `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`, add a helper beside `PumpPlays`:

```csharp
    // Like PumpPlays, but records the alert flag the playback loop reports for each job — the bit
    // the Wyoming host puts on audio-start for the satellite's sink selection.
    private static (Task Pump, Func<IReadOnlyList<bool>> Flags) PumpRecordsAlertFlags(
        SatelliteSession session, FakeTimeProvider time)
    {
        var flags = new List<bool>();
        var pump = session.RunPlaybackLoopAsync(
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            time,
            onAudioStart: (_, alert, _) =>
            {
                lock (flags) { flags.Add(alert); }
                return Task.CompletedTask;
            });
        return (pump, () => { lock (flags) { return flags.ToList(); } });
    }
```

and the tests:

```csharp
    // A timer must ring on the satellite's non-attenuated alert route, not at the calibrated
    // conversational voice level — that per-satellite level is exactly what makes a kitchen
    // countdown inaudible.
    [Fact]
    public async Task Start_Timer_MarksThePlaybackJobAsAnAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var (pump, flags) = PumpRecordsAlertFlags(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "eggs",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => flags().Count >= 1, TimeSpan.FromSeconds(5));
        flags()[0].ShouldBeTrue();

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }

    // Alarms take the same route. Their wake-up ramp is a separate, within-alert gain and is
    // deliberately unaffected — the routing change lifts the ceiling the ramp climbs toward.
    [Fact]
    public async Task Start_Alarm_MarksThePlaybackJobAsAnAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "bedroom-01");
        var (pump, flags) = PumpRecordsAlertFlags(h.Sessions.Get("bedroom-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "bedroom-01" },
                Text = "wake up",
                Kind = AnnounceKind.Alarm,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => flags().Count >= 1, TimeSpan.FromSeconds(5));
        flags()[0].ShouldBeTrue();

        h.Sessions.Get("bedroom-01")!.CompletePlayback();
        await pump;
    }
```

> Both tests end with `CompletePlayback(); await pump;` — that is how every existing test in this file
> ends the loop, and awaiting the pump matters: an unobserved faulted playback task leaks across
> xUnit's parallel test classes.

In `Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs`, add:

```csharp
    // A plain announcement (a download finishing, a reminder read out) is NOT an alert: it must
    // keep the calibrated per-satellite voice level rather than ringing at full scale.
    [Fact]
    public async Task Announce_PlainAnnouncement_IsNotMarkedAsAnAlert()
    {
        var (sut, sessions) = BuildSut(("kitchen-01", "Kitchen"));
        var session = sessions.Get("kitchen-01")!;
        var flags = new List<bool>();
        var pump = session.RunPlaybackLoopAsync(
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            onAudioStart: (_, alert, _) =>
            {
                lock (flags) { flags.Add(alert); }
                return Task.CompletedTask;
            });

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "download done" },
            CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (flags.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        flags.ShouldHaveSingleItem();
        flags[0].ShouldBeFalse();

        session.CompletePlayback();
        await pump;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests|FullyQualifiedName~AnnouncementServiceTests"
```

Expected: the two `InsistentAnnouncementControllerTests` FAIL with `flags()[0]` being `False` (the job is not yet marked). The `AnnouncementServiceTests` one should already PASS — it pins the default and guards against the flag being set too broadly.

- [ ] **Step 3: Mark the controller's jobs**

In `McpChannelVoice/Services/InsistentAnnouncementController.cs`, `BuildJob`:

```csharp
    private PlaybackJob BuildJob(
        string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session, double gain) =>
        new(
            Label: $"alarm:{announcementId}",
            Priority: AnnouncePriority.High,
            // The only place an alert is minted. This controller handles exactly the insistent
            // announces — timers and alarms — so the satellite's non-attenuated route is reached
            // by those and nothing else. AnnouncePriority.High is deliberately NOT the marker:
            // approval prompts and wake announcements share it and must stay at voice level.
            Alert: true,
            Audio: Replay(buffered, gain),
```

> Keep the remaining named arguments (`OnStarted`, `OnPreempted`) exactly as they are; only the `Alert: true` line is new.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"
```

Expected: PASS — every `McpChannelVoice` unit test green.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add McpChannelVoice/Services/InsistentAnnouncementController.cs Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs
git commit -m "feat(voice): mark insistent announces as alerts

Timers and alarms route to the satellite's non-attenuated sink; plain
announcements, download notifications and approval prompts keep the calibrated
voice level. The insistent controller is the single producer of the flag."
```

---

## Task 7: Alarm earcon at 0.9 amplitude

**Files:**
- Modify: `McpChannelVoice/Services/AlarmTone.cs:11`
- Test: `Tests/Unit/McpChannelVoice/AlarmToneTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing consumed downstream.

**Context:** `AlarmTone.Amplitude` scales the generated sine before conversion to `short`. At 0.5 the earcon sits 6 dB below the spoken text that follows it, so the attention-grabbing part is the quiet part. A pure sine does not clip at high amplitude, and `Segment` already applies a 10 ms fade in/out to each voiced segment, so 0.9 stays click-free while leaving a little headroom under a PipeWire mixer that may also be carrying ducked music.

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/McpChannelVoice/AlarmToneTests.cs`:

```csharp
    // The earcon is the attention-grabbing part of an alert, so it must sit near the level of the
    // speech that follows rather than 6 dB below it. Headroom is deliberate: a little is left for
    // the PipeWire mixer, which may be carrying ducked music underneath.
    [Theory]
    [InlineData(AnnounceKind.Alarm)]
    [InlineData(AnnounceKind.Timer)]
    public void Pcm_PeaksNearFullScale(AnnounceKind kind)
    {
        var pcm = AlarmTone.Pcm(kind);

        var peak = Enumerable.Range(0, pcm.Length / 2)
            .Select(i => Math.Abs((int)(short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8))))
            .Max();
        peak.ShouldBeGreaterThan((int)(short.MaxValue * 0.85));
        peak.ShouldBeLessThan(short.MaxValue);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~AlarmToneTests"
```

Expected: FAIL — both theory cases, `peak` around `16383` (0.5 × 32767) which is not greater than `27851`.

- [ ] **Step 3: Raise the amplitude**

In `McpChannelVoice/Services/AlarmTone.cs`:

```csharp
    // Near full scale on purpose: the earcon is the attention-grabbing part of an alert and used
    // to sit 6 dB under the speech that follows it. A pure sine does not clip here, and Segment's
    // 10 ms fades keep the edges click-free; the remaining headroom is for the PipeWire mixer.
    private const double Amplitude = 0.9;
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~AlarmToneTests"
```

Expected: PASS — 5 tests (3 existing + 2 theory cases). `Pcm_IsNonSilentAndBounded`'s `< short.MaxValue` no-clipping assertion must still hold.

- [ ] **Step 5: Commit**

```bash
cd /home/dethon/repos/agent
git add McpChannelVoice/Services/AlarmTone.cs Tests/Unit/McpChannelVoice/AlarmToneTests.cs
git commit -m "feat(voice): raise the alarm/timer earcon to 0.9 amplitude

The earcon sat 6 dB below the speech that follows it, so the attention-grabbing
part of an alert was the quiet part."
```

---

## Task 8: Companion fix — playback latency flags on the music drop-in

**Files:**
- Modify: `scripts/provision-satellite-rs.sh:346`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing — an independent provisioning correctness fix, deliberately its own commit.

**Context:** `92b9e9fa` (2026-06-11 perf pass) added `--start-delay=100000 -F 50000` to the playback command but touched only `satellite/src/config.rs` and `satellite/deploy/nabu-satellite.service`. Music units get a drop-in that **overrides that ExecStart wholesale**, so the fix never reached the deployed office satellite — against `satellite/CLAUDE.md`'s "keep them when overriding devices". The mic half of the same pass *did* reach the drop-in (`f18b01c0` inlined `-F 20000`), so this is an oversight. Nothing catches it: `config.rs`'s `defaults_are_sane` asserts on the default string, which the drop-in replaces.

The effect size through `pcm.tts` → softvol → `pipewire` is uncertain — the PipeWire ALSA plugin negotiates buffering from the graph quantum and may already be short. The change is harmless either way and restores the documented invariant. This is **not** blocked on measuring it.

- [ ] **Step 1: Add the flags**

In `scripts/provision-satellite-rs.sh`, in the `nabu-satellite.service.d/pipewire.conf` heredoc, change the `--snd-command` line:

```bash
  --snd-command "aplay -D tts -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000" \\
```

- [ ] **Step 2: Verify the script still parses and the flags are present**

```bash
cd /home/dethon/repos/agent
bash -n scripts/provision-satellite-rs.sh && echo "syntax OK"
grep -c -- '--start-delay=100000 -F 50000' scripts/provision-satellite-rs.sh
```

Expected: `syntax OK`, and the grep count is `1`.

- [ ] **Step 3: Commit**

```bash
cd /home/dethon/repos/agent
git add scripts/provision-satellite-rs.sh
git commit -m "fix(satellite): carry the playback latency flags into the music drop-in

92b9e9fa added --start-delay=100000 -F 50000 to config.rs and the base unit but
not to provisioning, and music units override that ExecStart wholesale — so the
fix never reached the deployed office satellite, against the CLAUDE.md
invariant. The mic half of the same perf pass did get inlined (f18b01c0)."
```

---

## Task 9: Provisioning — the `alert` softvol, `ALERT_VOLUME`, master at 100 %

**Files:**
- Modify: `scripts/provision-satellite-rs.sh` — header docs (`:5`, `:22-25`), ssh env line (`:124`), `asound.conf` heredoc (`:292-310`), master volume (`:315`), calibration block (`:317-325`), drop-in ExecStart (`:343-350`)

**Interfaces:**
- Consumes: `--alert-snd-command` from Task 1.
- Produces: the deployed level chain — three source softvols (`Music`, `TTS`, `Alert`) under a master at 100 %.

**Context:** A softvol control only materializes on the first open of its PCM, which is why `TTS` is preceded by 1 s of silence through `pcm.tts` before `amixer sset`. `Alert` needs the same three steps. The master at `0.8` is a provisioning-time "sane default" with no function now that all calibration lives in the source knobs — and it caps how loud a bypassed alert can get, so it moves to `1.0`.

**Deployment consequence to keep in the header docs:** raising the master makes music and the agent voice louder too, not just alerts. `TTS_VOLUME` wants retuning downward on an already-calibrated unit.

- [ ] **Step 1: Add `ALERT_VOLUME` to the usage header**

Change line 5 and extend the softvol paragraph (lines 22-25):

```bash
#   Music satellite: MUSIC_HUB=<snapserver-host> MUSIC_ROOM=<player-name> [TTS_VOLUME=<pct>] \
#                    [ALERT_VOLUME=<pct>] scripts/provision-satellite-rs.sh <user@host>
```

```bash
# On music units the satellite's own playback flows through per-source ALSA softvols on the speaker
# card, under a master (the PipeWire sink) held at 100% — all calibration lives in the source knobs:
#   TTS   (TTS_VOLUME%, default 75)  agent voice + cues; the volume knob for amp HATs that have none
#   Alert (ALERT_VOLUME%, default 100) timers/alarms, so an alert bypasses the conversational level
#   Music (ducked live by the satellite while it is listening or speaking)
# Tune live: amixer -c <card> sset TTS <pct>% ; persist: sudo alsactl store.
# NOTE: the master used to sit at 0.8 and now sits at 1.0, so re-provisioning an already-calibrated
# unit makes music AND the agent voice louder, not just alerts — retune TTS_VOLUME to compensate.
```

- [ ] **Step 2: Pass `ALERT_VOLUME` over ssh**

Change line 121's comment and line 124:

```bash
# Quoted heredoc + MIC/MUSIC_HUB/MUSIC_ROOM/TTS_VOLUME/ALERT_VOLUME/THRESHOLD/TRIGGER_LEVEL env
```

```bash
ssh "${SSHOPTS[@]}" "$host" MIC="${mic}" MUSIC_HUB="${MUSIC_HUB:-}" MUSIC_ROOM="${MUSIC_ROOM:-}" TTS_VOLUME="${TTS_VOLUME:-75}" ALERT_VOLUME="${ALERT_VOLUME:-100}" THRESHOLD="${threshold}" TRIGGER_LEVEL="${trigger_level}" bash -se <<'EOF'
```

- [ ] **Step 3: Add `pcm.alert` to `asound.conf`**

Extend the comment above the heredoc and add the block after `pcm.tts`:

```bash
    # `music` PCM: snapclient -> a softvol the satellite ducks (amixer -c <card> sset Music <pct>%)
    # -> PipeWire -> speaker. `tts` PCM: the satellite's own playback (replies + cues) -> a softvol
    # holding the calibrated agent-voice level (TTS_VOLUME) -> PipeWire -> speaker, independent
    # of music — the volume knob for amp HATs that have none (e.g. the MiniAmp). `alert` PCM: the
    # same shape at ALERT_VOLUME (default 100), used ONLY for hub-marked timer/alarm streams, so
    # an alert is not capped by the conversational voice level. All three CONTROLs are stored on
    # the speaker card (HAT when present, else the jack); the audio itself flows through PipeWire
    # either way. NO pcm.!default (PipeWire's own default stands); capture stays direct plughw.
```

```
pcm.alert {
    type softvol
    slave.pcm "pipewire"
    control { name "Alert" card ${outctl} }
    min_dB -51.0
    max_dB 0.0
    resolution 256
}
```

> Add it **inside** the existing `<<ASOUND` … `ASOUND` heredoc, after the `pcm.tts` block and before the closing `ASOUND` line. `${outctl}` is expanded by the heredoc exactly as the other two blocks rely on.

- [ ] **Step 4: Raise the master and calibrate `Alert`**

Change line 315:

```bash
    # Master at FULL: every level lives in the per-source softvols below (Music / TTS / Alert), so
    # a second attenuation here would only cap how loud an alert can get.
    XDG_RUNTIME_DIR=/run/user/$uid wpctl set-volume @DEFAULT_AUDIO_SINK@ 1.0 2>/dev/null || true
```

Extend the calibration block (replacing lines 317-325's `aplay`/`amixer`/`alsactl` trio):

```bash
    # Calibrate the source levels: a softvol CONTROL only materializes on first open of its PCM,
    # so play 1 s of silence through each, then set the level (re-asserted on every provision,
    # like the master above) and store ALSA state so it survives a power cut, not just a clean
    # shutdown. Runs as the login user: on Raspberry Pi OS the default user is in the `audio`
    # group at login (needed to create the control); a first-ever provision under a user only just
    # added to `audio` above would fail here — re-login (rerun the script) fixes it.
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D tts -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset TTS "${TTS_VOLUME}%"
    XDG_RUNTIME_DIR=/run/user/$uid timeout 10 aplay -D alert -r 22050 -c 1 -f S16_LE -t raw -d 1 /dev/zero
    amixer -c "${outctl}" sset Alert "${ALERT_VOLUME}%"
    sudo alsactl store
```

- [ ] **Step 5: Point the drop-in's alert sink at `pcm.alert`**

In the `pipewire.conf` drop-in heredoc, add the flag line directly after `--snd-command`:

```bash
  --alert-snd-command "aplay -D alert -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000" \\
```

Extend the comment above the drop-in:

```bash
  # nabu-satellite drop-in: replies + cues -> the `tts` softvol (calibrated agent-voice level),
  # timer/alarm streams -> the `alert` softvol (ALERT_VOLUME, default 100) so they are not capped
  # by that level; both -> PipeWire (speaker). Ducks the `Music` softvol while active; XDG so
  # aplay reaches PipeWire. Overrides the base voice ExecStart.
```

> The base `satellite/deploy/nabu-satellite.service` deliberately gets **no** `--alert-snd-command`: a voice-only unit has no softvols, and the flag defaults to `--snd-command`. This also keeps the `sed` at line 371 (`/^[[:space:]]*--snd-command/ s#-D [^ ]*#-D ${snddev}#`) matching exactly one line.

- [ ] **Step 6: Verify the script parses and every piece landed**

```bash
cd /home/dethon/repos/agent
bash -n scripts/provision-satellite-rs.sh && echo "syntax OK"
grep -c 'pcm.alert {' scripts/provision-satellite-rs.sh          # expect 1
grep -c 'sset Alert' scripts/provision-satellite-rs.sh            # expect 1
grep -c 'aplay -D alert' scripts/provision-satellite-rs.sh        # expect 2 (materialize + drop-in)
grep -c 'set-volume @DEFAULT_AUDIO_SINK@ 1.0' scripts/provision-satellite-rs.sh  # expect 1
grep -c 'set-volume @DEFAULT_AUDIO_SINK@ 0.8' scripts/provision-satellite-rs.sh  # expect 0
grep -c 'ALERT_VOLUME' scripts/provision-satellite-rs.sh          # expect >= 5 (docs + ssh + set)
```

Expected: `syntax OK` and each count as annotated. The `0.8` count being `0` is the load-bearing one — if it is still `1`, the master was not actually changed. `grep -c` counts matching *lines*, so the `ALERT_VOLUME` figure is a floor, not an exact count; don't chase it to a specific number.

- [ ] **Step 7: Commit**

```bash
cd /home/dethon/repos/agent
git add scripts/provision-satellite-rs.sh
git commit -m "feat(satellite): alert softvol at ALERT_VOLUME, master to 100%

Music units gain a third source softvol (Alert, default 100%) fed by
--alert-snd-command, so a timer or alarm bypasses the calibrated TTS voice
level. The PipeWire master drops its arbitrary 0.8 attenuation: all calibration
lives in the source knobs, and a second cut there only capped how loud an alert
could get. Re-provisioning an already-tuned unit makes music and the voice
louder too — retune TTS_VOLUME."
```

---

## Task 10: Architecture docs

**Files:**
- Modify: `CLAUDE.md` (Voice Satellite Architecture section)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

**Context:** `satellite/CLAUDE.md` was updated in Task 3 (protocol + latency-flags invariant). The root `CLAUDE.md`'s **Voice Satellite Architecture** section describes the pipeline end to end and says nothing about alert routing or the softvol chain; the **Timers Architecture** section is accurate as-is (fire still POSTs `/api/voice/announce`) and needs no change.

- [ ] **Step 1: Document the alert route**

In `CLAUDE.md`, in the **Voice Satellite Architecture** section, append a paragraph after the pipeline description:

```markdown
**Alert routing.** Insistent announces — timers and alarms, i.e. exactly the `/api/voice/announce`
requests carrying `insistent` — are marked `alert: true` on the Wyoming `audio-start` (protocol
1.5, `WyomingSatelliteHost.BuildAudioStart`; `InsistentAnnouncementController` is the only producer,
via `PlaybackJob.Alert`). The satellite plays a marked stream on `--alert-snd-command` instead of
`--snd-command`: on music units a non-attenuated `alert` ALSA softvol, so an alert is not capped by
the calibrated conversational `TTS` level. `AnnouncePriority.High` is deliberately not the marker —
approval prompts share it. The flag defaults to false everywhere, so ordinary replies, plain
announcements and a pre-1.5 satellite are unaffected, and an unopenable alert device falls back to
the normal sink rather than dropping the connection. The satellite's level chain is three per-source
softvols (`Music`, `TTS`, `Alert`) under a PipeWire master held at 100 %; see
`scripts/provision-satellite-rs.sh` for `TTS_VOLUME` / `ALERT_VOLUME`.
```

- [ ] **Step 2: Verify the full test suites are green**

```bash
cd /home/dethon/repos/agent/satellite && cargo test
```

Expected: 85 lib tests + 2 `spike_wake` tests pass, 0 failed.

```bash
dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~Unit"
```

Expected: all unit tests pass. Judge failures by **type**, not count — see the known-baseline note in *Deployment* below.

- [ ] **Step 3: Commit**

```bash
cd /home/dethon/repos/agent
git add CLAUDE.md
git commit -m "docs: describe alert routing in the voice satellite architecture"
```

---

## Deployment (manual, after all tasks)

Not part of the plan's commits — run these once the branch is merged.

1. **Build the satellite:** `satellite/scripts/build-release.sh` — never bare `cargo zigbuild` (the fp16 CC shim is mandatory).
2. **Re-provision the office unit:** `scripts/provision-satellite-rs.sh <user@speaker-fran-office>` with the unit's existing `MUSIC_HUB` / `MUSIC_ROOM` / `TTS_VOLUME` env. This writes `asound.conf` with `pcm.alert`, sets the master to 1.0, materializes and sets `Alert`, and rewrites the drop-in.
3. **Redeploy `mcp-channel-voice`** on ai370 (192.168.5.45).
4. **Retune the voice:** the master went 0.8 → 1.0, so expect music and the agent voice to be louder. `amixer -c sndrpihifiberry sset TTS <pct>%` then `sudo alsactl store`.
5. **Verify:** set a 1-minute timer by voice and confirm it rings noticeably louder than a spoken reply. `amixer -c sndrpihifiberry sget Alert` should read 100 %.

Protocol back-compat makes steps 1-3 order-independent: a new hub's `alert` field is ignored by an old satellite, and a new satellite treats a missing field as false.

**Optional measurement** (does not gate anything): `aplay -D tts -v` on the Pi prints the negotiated `buffer_size` / `period_size` / `start_threshold`, settling how much Task 8's latency flags actually change through the PipeWire ALSA plugin.

**Known test baseline:** the `McpAgent` cleanup test fails consistently and pre-dates this work; `dotnet format --verify-no-changes` is permanently dirty on top-level `Program.cs` files. Neither is a regression from this change.
