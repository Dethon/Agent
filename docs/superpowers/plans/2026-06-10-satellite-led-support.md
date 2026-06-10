# Satellite LED Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `nabu-satellite` lights an LED while the satellite is active (turn start → end of TTS playback, announcements included), on either the reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs (SPI, default) or a single GPIO-wired LED — degrading gracefully to LED-less operation when no hardware is present.

**Architecture:** The state machine publishes semantic `LedState` values (`Idle`/`Listening`/`Thinking`/`Speaking`) on a `tokio::sync::watch` channel; a per-connection render task (spawned/aborted like the button guard) owns the hardware backend and maps states to light (V1: `Idle` → off, everything else → steady on). Approved spec: `docs/superpowers/specs/2026-06-10-satellite-led-design.md`. Parent plan: `docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md` (milestones 0–5.2 complete; this is an additive feature on `rust-satellites`).

**Tech Stack:** Rust, `tokio` (`sync`/`time` features already enabled; the `test-util` feature is added as a **dev-dependency** in Task 4 for paused-clock tests), `rppal` 0.22 `spi` + `gpio` modules (already a dependency — **zero new crates**), `pico-args`.

**Verified hardware facts the code relies on** (do not re-derive):
- HAT: 3× APA102-2020 on SPI0, no chip-select wired; reference drivers use **`/dev/spidev0.1` at 8 MHz, mode 0** (`Bus::Spi0, SlaveSelect::Ss1`). No power-enable GPIO exists on this HAT.
- APA102 update for n LEDs: 4-byte zero start frame + per-LED `0xE0|brightness(5-bit), B, G, R` + 4-byte zero end frame (sufficient for n ≤ 64; doubles as SK9822 latch). **3 LEDs = 20 bytes.**
- rppal `OutputPin` resets the pin on drop (LED off); `into_output_low()` claims the pin already-off.
- Run all commands from `satellite/` unless stated otherwise. RED steps in Rust often fail as *compile errors* — that counts as the failing test.

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `satellite/src/config.rs` | Modify | `LedConfig` enum, `led` field, `--no-led`/`--led-gpio` flags, testable `parse()` |
| `satellite/src/led.rs` | Create | Everything LED: `LedState`, `apa102_frame`, `LedBackend` (Gpio/Spi/Probe), `spawn_led` + render task |
| `satellite/src/main.rs` | Modify | `mod led;` declaration only |
| `satellite/src/satellite/state_machine.rs` | Modify | `Ctx` struct (cues/cfg/led refs), publish points, channel + guard wiring |
| `satellite/Cargo.toml` | Modify | rppal comment line + `[dev-dependencies]` tokio `test-util` feature (no new crates) |
| `satellite/deploy/nabu-satellite.service` | Modify | `SupplementaryGroups` + comment |
| `satellite/README.md` | Modify | Status LED section, `--no-led` in WSL/docker examples |
| `docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md` | Modify | One cross-reference line |

---

### Task 1: `LedConfig` + CLI parsing

**Files:**
- Modify: `satellite/src/config.rs`

- [ ] **Step 1.1: Write the failing tests**

In `config.rs`, make `from_args` testable by splitting out `parse` (pico-args supports `Arguments::from_vec`, which takes args *without* the binary name). Add to the existing `tests` module:

```rust
fn args(v: &[&str]) -> pico_args::Arguments {
    pico_args::Arguments::from_vec(v.iter().map(std::ffi::OsString::from).collect())
}

#[test]
fn led_defaults_to_spi() {
    assert_eq!(Config::default().led, LedConfig::Spi);
}

#[test]
fn led_gpio_flag_parses() {
    let c = Config::parse(args(&["--led-gpio", "22"])).unwrap();
    assert_eq!(c.led, LedConfig::Gpio(22));
}

#[test]
fn no_led_flag_parses() {
    let c = Config::parse(args(&["--no-led"])).unwrap();
    assert_eq!(c.led, LedConfig::None);
}
```

- [ ] **Step 1.2: Run tests to verify they fail**

Run: `cargo test config::tests`
Expected: COMPILE ERROR — `LedConfig` and `Config::parse` do not exist.

- [ ] **Step 1.3: Implement**

In `config.rs`, below `ButtonConfig`:

```rust
/// Where the activity LED lives. Optional hardware: init failure degrades to LED-less.
#[derive(Clone, Debug, PartialEq)]
pub enum LedConfig {
    None,
    /// The reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs on SPI0 (/dev/spidev0.1).
    Spi,
    /// Single indicator LED on a free GPIO pin (BCM numbering), active-high.
    Gpio(u8),
}
```

Add the field to `Config` (after `button`):

```rust
    pub led: LedConfig,         // activity LED; default = the HAT's APA102s, --no-led / --led-gpio override
```

…and to `Default` (after the `button` line):

```rust
            led: LedConfig::Spi, // reSpeaker HAT onboard APA102s; override with --led-gpio or --no-led
```

Split `from_args` so flag parsing is testable, and add the LED flags (after the button block, before `pa.finish()`):

```rust
    /// Flags: --listen --mic-command --snd-command --threshold --no-wake
    ///        --button-gpio <pin> | --button-evdev <device>:<keycode> | --no-button
    ///        --no-led | --led-gpio <pin>
    ///        --preroll-ms <ms> --wake-preroll-ms <ms> --no-awake-cue --no-done-cue
    pub fn from_args() -> anyhow::Result<Self> {
        Self::parse(pico_args::Arguments::from_env())
    }

    fn parse(mut pa: pico_args::Arguments) -> anyhow::Result<Self> {
        // Move the existing from_args body here with TWO changes:
        //  1. DELETE its first line `let mut pa = pico_args::Arguments::from_env();` —
        //     `pa` is now the parameter. Keeping it would shadow the parameter: the tests
        //     would read the test binary's real argv (the `config::tests` filter lands in
        //     pa.finish() and trips the ensure!), and clippy -D warnings fails on the
        //     unused/needlessly-mut parameter.
        //  2. Keep `let mut c = Config::default();` and the rest unchanged, then add this
        //     block before `pa.finish()`:
        if pa.contains("--no-led") {
            c.led = LedConfig::None;
        } else if let Some(pin) = pa.opt_value_from_str::<_, u8>("--led-gpio")? {
            c.led = LedConfig::Gpio(pin);
        }
        // (no --led-spi flag: Spi is the default; --led-gpio's absence restores it)
        let rest = pa.finish();
        anyhow::ensure!(rest.is_empty(), "unknown arguments: {rest:?}");
        Ok(c)
    }
```

- [ ] **Step 1.4: Run tests to verify they pass**

Run: `cargo test config::tests`
Expected: PASS (existing `defaults_are_sane` plus the 3 new tests).

- [ ] **Step 1.5: Commit**

```bash
git add satellite/src/config.rs
git commit -m "feat(satellite): LedConfig with --no-led/--led-gpio flags, default HAT APA102s"
```

---

### Task 2: `led.rs` — `LedState` + APA102 frame encoder

**Files:**
- Create: `satellite/src/led.rs`
- Modify: `satellite/src/main.rs` (add `mod led;` after `mod gpio;`)

- [ ] **Step 2.1: Write the failing test**

Create `satellite/src/led.rs`:

```rust
//! Activity LED: the state machine publishes semantic LedState values on a watch channel;
//! a per-connection render task owns the hardware backend and maps states to light.
//! V1 policy: Idle -> off, everything else -> steady on.

/// Semantic satellite phase, published by the state machine. The render task — never the
/// state machine — decides what each phase looks like, so future blink patterns touch
/// only this module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LedState { Idle, Listening, Thinking, Speaking }

#[cfg(test)]
mod tests {
    use super::*;

    // Golden bytes: 4-byte zero start frame, per-LED 0xE0|brightness then B,G,R,
    // 4-byte zero end frame (>= n/2 clock pulses for n<=64, doubles as SK9822 latch).
    #[test]
    fn apa102_frame_golden_bytes() {
        let f = apa102_frame((0, 0, 255), 8, 3);
        assert_eq!(f.len(), 20);
        assert_eq!(&f[..4], &[0, 0, 0, 0]);
        for led in 0..3 {
            assert_eq!(&f[4 + led * 4..8 + led * 4], &[0xE8, 255, 0, 0]); // 0xE0|8, B, G, R
        }
        assert_eq!(&f[16..], &[0, 0, 0, 0]);
    }

    #[test]
    fn apa102_frame_masks_brightness_to_5_bits() {
        let f = apa102_frame((1, 2, 3), 0xFF, 1);
        assert_eq!(f[4], 0xFF); // 0xE0 | (0xFF & 0x1F) = 0xFF
        assert_eq!(&f[5..8], &[3, 2, 1]); // B, G, R order
    }
}
```

Add `mod led;` to `main.rs` (after `mod gpio;`).

- [ ] **Step 2.2: Run test to verify it fails**

Run: `cargo test led::tests`
Expected: COMPILE ERROR — `apa102_frame` not found.

- [ ] **Step 2.3: Implement the encoder**

Add to `led.rs` above the tests:

```rust
/// V1 render constants: one fixed look, change here only. (--led-color is deferred.)
const LED_COUNT: usize = 3;                  // the HAT has exactly 3 APA102-2020s
const LED_COLOR: (u8, u8, u8) = (0, 0, 255); // RGB: blue
const LED_BRIGHTNESS: u8 = 8;                // APA102 global brightness, of 31

/// Full APA102 update for `n` daisy-chained LEDs, all set to the same color.
/// Layout: 32-bit zero start frame; per LED `0xE0|brightness(5-bit), B, G, R`;
/// 32-bit zero end frame (sufficient clock pulses for n <= 64; doubles as the SK9822 latch).
fn apa102_frame((r, g, b): (u8, u8, u8), brightness: u8, n: usize) -> Vec<u8> {
    let mut out = vec![0u8; 4];
    for _ in 0..n {
        out.extend_from_slice(&[0xE0 | (brightness & 0x1F), b, g, r]);
    }
    out.extend_from_slice(&[0, 0, 0, 0]);
    out
}
```

(`LED_COUNT`/`LED_COLOR`/`LED_BRIGHTNESS` are consumed in Task 3 — if the compiler warns about dead code at this intermediate step, that's expected and resolves in Task 3.)

- [ ] **Step 2.4: Run test to verify it passes**

Run: `cargo test led::tests`
Expected: PASS (2 tests).

- [ ] **Step 2.5: Commit**

```bash
git add satellite/src/led.rs satellite/src/main.rs
git commit -m "feat(satellite): LedState and APA102 frame encoder"
```

---

### Task 3: `LedBackend` — Gpio / Spi / test Probe

**Files:**
- Modify: `satellite/src/led.rs`

- [ ] **Step 3.1: Write the failing tests**

Add to the `tests` module in `led.rs`:

```rust
    use crate::config::LedConfig;
    use std::sync::{Arc, Mutex};

    fn probe() -> (Arc<Mutex<Vec<bool>>>, LedBackend) {
        let log = Arc::new(Mutex::new(Vec::new()));
        (log.clone(), LedBackend::Probe(log))
    }

    #[test]
    fn probe_backend_records_writes() {
        let (log, mut b) = probe();
        b.set(true).unwrap();
        b.set(false).unwrap();
        assert_eq!(*log.lock().unwrap(), vec![true, false]);
    }

    #[test]
    fn none_config_yields_no_backend() {
        assert!(build_backend(&LedConfig::None).unwrap().is_none());
    }
```

- [ ] **Step 3.2: Run tests to verify they fail**

Run: `cargo test led::tests`
Expected: COMPILE ERROR — `LedBackend` / `build_backend` not found.

- [ ] **Step 3.3: Implement the backends**

Add to `led.rs` (below the encoder). Imports at the top of the file: `use crate::config::LedConfig;`.

```rust
/// The hardware behind the light. Owned by the render task; dropped on connection end.
pub enum LedBackend {
    /// Single LED on a GPIO pin, active-high. rppal's reset-on-drop releases the pin (off).
    Gpio(rppal::gpio::OutputPin),
    /// The HAT's APA102 chain on /dev/spidev0.1. Drop writes the off frame explicitly.
    Spi(rppal::spi::Spi),
    #[cfg(test)]
    Probe(std::sync::Arc<std::sync::Mutex<Vec<bool>>>),
}

impl LedBackend {
    fn set(&mut self, on: bool) -> anyhow::Result<()> {
        match self {
            LedBackend::Gpio(pin) => {
                if on { pin.set_high() } else { pin.set_low() }
                Ok(())
            }
            LedBackend::Spi(spi) => {
                let (color, brightness) = if on { (LED_COLOR, LED_BRIGHTNESS) } else { ((0, 0, 0), 0) };
                spi.write(&apa102_frame(color, brightness, LED_COUNT))?;
                Ok(())
            }
            #[cfg(test)]
            LedBackend::Probe(log) => {
                log.lock().unwrap().push(on);
                Ok(())
            }
        }
    }
}

impl Drop for LedBackend {
    // Gpio relies on rppal's reset-on-drop; the APA102s latch their last frame, so Spi
    // must write the off frame explicitly. Runs on task abort (connection end/supersede).
    fn drop(&mut self) {
        if matches!(self, LedBackend::Spi(_)) {
            let _ = self.set(false);
        }
    }
}

/// Ok(None) when no LED is configured. Errors bubble to spawn_led, which warns and
/// runs LED-less — missing hardware must never take the satellite down.
fn build_backend(cfg: &LedConfig) -> anyhow::Result<Option<LedBackend>> {
    match cfg {
        LedConfig::None => Ok(None),
        LedConfig::Gpio(pin) => {
            // into_output_low claims the pin already-off (the init-clear for this backend).
            let pin = rppal::gpio::Gpio::new()?.get(*pin)?.into_output_low();
            Ok(Some(LedBackend::Gpio(pin)))
        }
        LedConfig::Spi => {
            use rppal::spi::{Bus, Mode, SlaveSelect, Spi};
            // Ss1 -> /dev/spidev0.1: the HAT wires no chip-select; this matches Seeed's own driver.
            let spi = Spi::new(Bus::Spi0, SlaveSelect::Ss1, 8_000_000, Mode::Mode0)?;
            let mut backend = LedBackend::Spi(spi);
            backend.set(false)?; // clear stale light from a crashed predecessor
            Ok(Some(backend))
        }
    }
}
```

- [ ] **Step 3.4: Run tests to verify they pass**

Run: `cargo test led::tests`
Expected: PASS (4 tests). Note: the Gpio/Spi arms cannot run in CI (no `/dev/gpiomem`//dev/spidev`) — they validate on-device in parent-plan Milestone 5, like the button.

- [ ] **Step 3.5: Update the Cargo.toml comment and commit**

In `satellite/Cargo.toml`, update the rppal comment line to:

```toml
rppal = "0.22"         # GpioButton + activity LED (GPIO out / APA102 over SPI); pure-Rust, static-musl-clean, Linux-only
```

```bash
git add satellite/src/led.rs satellite/Cargo.toml
git commit -m "feat(satellite): LED backends — GPIO pin and HAT APA102 over SPI, off-on-drop"
```

---

### Task 4: Render task + `spawn_led`

**Files:**
- Modify: `satellite/src/led.rs`

- [ ] **Step 4.1: Write the failing tests**

First add the paused-clock test feature to `satellite/Cargo.toml` — `start_paused` and `tokio::time::advance` are gated behind tokio's `test-util` feature. As a dev-dependency it feature-unifies into **test builds only**; the release/cross-build binary (Step 7.3) keeps the lean feature set:

```toml
[dev-dependencies]
tokio = { version = "1", features = ["test-util"] }  # start_paused/time::advance for LED render tests
```

Then add to the `tests` module in `led.rs`:

```rust
    use tokio::sync::watch;

    // Poll-with-yield instead of sleeping: these tests run under start_paused, where
    // yield_now keeps the runtime busy (no auto-advance) while the render task catches up.
    async fn wait_probe(log: &Arc<Mutex<Vec<bool>>>, expect: &[bool]) {
        for _ in 0..100 {
            if log.lock().unwrap().as_slice() == expect { return; }
            tokio::task::yield_now().await;
        }
        panic!("probe never reached {expect:?}, got {:?}", log.lock().unwrap());
    }

    #[tokio::test(start_paused = true)]
    async fn render_lights_non_idle_and_writes_only_on_change() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let _task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Listening).unwrap();
        wait_probe(&log, &[true]).await;
        // Thinking and Speaking keep the light on -> no extra writes; Idle turns it off.
        tx.send(LedState::Thinking).unwrap();
        tx.send(LedState::Speaking).unwrap();
        tx.send(LedState::Idle).unwrap();
        wait_probe(&log, &[true, false]).await;
    }

    #[tokio::test(start_paused = true)]
    async fn thinking_goes_dark_after_fallback_and_relights_on_late_reply() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let _task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Thinking).unwrap();
        // When wait_probe sees the write, the render task has already polled (and thus
        // registered) the timeout future — set() and the await are one synchronous stretch.
        wait_probe(&log, &[true]).await;
        tokio::time::advance(THINKING_FALLBACK + std::time::Duration::from_secs(1)).await;
        wait_probe(&log, &[true, false]).await;
        tx.send(LedState::Speaking).unwrap(); // late reply still lights up
        wait_probe(&log, &[true, false, true]).await;
    }

    #[tokio::test(start_paused = true)]
    async fn sender_drop_turns_led_off_and_ends_task() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Speaking).unwrap();
        wait_probe(&log, &[true]).await;
        drop(tx); // connection ending
        task.await.unwrap();
        assert_eq!(*log.lock().unwrap(), vec![true, false]);
    }

    #[tokio::test]
    async fn none_config_spawns_no_task() {
        let (_tx, rx) = watch::channel(LedState::Idle);
        assert!(spawn_led(&LedConfig::None, rx).is_none());
    }
```

- [ ] **Step 4.2: Run tests to verify they fail**

Run: `cargo test led::tests`
Expected: COMPILE ERROR — `render_loop`, `THINKING_FALLBACK`, `spawn_led` not found.

- [ ] **Step 4.3: Implement the render task**

Add to `led.rs`. Extend imports: `use tokio::sync::watch;` and `use tracing::warn;`.

```rust
/// Display fallback: if a reply never arrives after a transcript (hub error/timeout — a
/// known deferred race), stop glowing after the hub's own 120 s reply timeout.
const THINKING_FALLBACK: std::time::Duration = std::time::Duration::from_secs(120);

/// Aborts the render task on drop (connection end/supersede), same idiom as the pumps;
/// the abort drops the backend, whose Drop turns the light off.
pub struct LedGuard(tokio::task::JoinHandle<()>);
impl Drop for LedGuard {
    fn drop(&mut self) { self.0.abort(); }
}

/// Build the configured backend and start the render task. None when no LED is configured
/// or the hardware is absent (one warning) — the satellite runs identically without it.
pub fn spawn_led(cfg: &LedConfig, rx: watch::Receiver<LedState>) -> Option<LedGuard> {
    let backend = match build_backend(cfg) {
        Ok(Some(b)) => b,
        Ok(None) => return None,
        Err(e) => { warn!("led unavailable: {e:#}"); return None; }
    };
    Some(LedGuard(tokio::spawn(render_loop(rx, backend))))
}

/// V1 policy: Idle -> off, everything else -> steady on. Writes only on transitions.
/// A write failure disables the LED for the rest of the connection (one warning, no spam);
/// the next connection re-initializes. LED problems never tear down a connection.
async fn render_loop(mut rx: watch::Receiver<LedState>, mut backend: LedBackend) {
    let mut lit = false;
    loop {
        let state = *rx.borrow_and_update();
        let want = state != LedState::Idle;
        if want != lit {
            if let Err(e) = backend.set(want) {
                warn!("led write failed, led disabled for this connection: {e:#}");
                return;
            }
            lit = want;
        }
        let changed = if state == LedState::Thinking {
            match tokio::time::timeout(THINKING_FALLBACK, rx.changed()).await {
                Err(_elapsed) => {
                    if lit {
                        if let Err(e) = backend.set(false) {
                            warn!("led write failed, led disabled for this connection: {e:#}");
                            return;
                        }
                        lit = false;
                    }
                    rx.changed().await // stay dark until the next state change
                }
                Ok(r) => r,
            }
        } else {
            rx.changed().await
        };
        if changed.is_err() { break; } // sender dropped -> connection ending
    }
    if lit { let _ = backend.set(false); }
}
```

- [ ] **Step 4.4: Run tests to verify they pass**

Run: `cargo test led::tests`
Expected: PASS (8 tests).

- [ ] **Step 4.5: Commit**

```bash
git add satellite/src/led.rs satellite/Cargo.toml
git commit -m "feat(satellite): LED render task — watch-driven steady light with 120s thinking fallback"
```

---

### Task 5: State machine publishes `LedState`

**Files:**
- Modify: `satellite/src/satellite/state_machine.rs`

- [ ] **Step 5.1: Write the failing tests**

This task also refactors the handler signatures: adding a `led` parameter to `handle_hub_event` would make 8 parameters and trip `clippy::too_many_arguments` (the repo gates on `-D warnings`), so the immutable per-connection refs move into a `Ctx` struct. New tests in the `tests` module of `state_machine.rs`:

```rust
    use crate::led::LedState;
    use tokio::sync::watch;

    fn test_cfg() -> Config {
        Config { snd_command: "cat >/dev/null".into(), ..Config::default() }
    }

    #[tokio::test]
    async fn turn_lifecycle_publishes_led_states() {
        let (mut a, _b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let cfg = test_cfg();
        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, cfg: &cfg, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        let mut playback: Option<PlaybackSink> = None;

        start_turn(&mut a, &mut mode, &ctx, &mut preroll).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        let transcript = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(transcript, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    // Announcements: audio-start arrives with no preceding turn and must still light the LED.
    #[tokio::test]
    async fn announcement_playback_publishes_speaking_then_idle() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let cfg = test_cfg();
        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, cfg: &cfg, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut playback: Option<PlaybackSink> = None;

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    #[tokio::test]
    async fn stale_transcript_publishes_no_led_state() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();
        let cfg = test_cfg();
        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, cfg: &cfg, led: &led_tx };
        let mut mode = Mode::Idle; // not Streaming -> transcript is stale
        let mut playback: Option<PlaybackSink> = None;

        let stale = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(stale, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert!(!led_rx.has_changed().unwrap(), "stale transcript must not touch the LED");
    }
```

- [ ] **Step 5.2: Update the existing call sites to the new signatures (the file stays uncompilable until 5.4 implements `Ctx` — that compile error is the RED)**

`start_turn` and `handle_hub_event` change signature; update **all** existing tests in this file to build a `Ctx`. The pattern, applied to each:

```rust
// start_turn_flushes_preroll_before_streaming:
let c = cues();
let cfg = Config::default();
let (led_tx, _led_rx) = watch::channel(LedState::Idle);
let ctx = Ctx { cues: &c, cfg: &cfg, led: &led_tx };
// ...
start_turn(&mut a, &mut mode, &ctx, &mut preroll).await.unwrap();
```

```rust
// transcript_ends_turn_with_audio_stop_and_rearms AND transcript_while_idle_is_a_noop:
let c = cues();
let cfg = Config::default();
let (led_tx, _led_rx) = watch::channel(LedState::Idle);
let ctx = Ctx { cues: &c, cfg: &cfg, led: &led_tx };
// ...
handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
```

In `survives_fragmented_hub_frames_under_mic_flood`, add to the `Config` literal (after `button: ...`):

```rust
            led: crate::config::LedConfig::None, // no /dev/spidev in CI; keep the log clean
```

(Note: a watch sender with zero receivers makes `send` return `Err` — the implementation uses `let _ = led.send(...)`, so tests that drop `_led_rx` still pass.)

- [ ] **Step 5.3: Run tests to verify they fail**

Run: `cargo test state_machine`
Expected: COMPILE ERROR — `Ctx` not found, `start_turn`/`handle_hub_event` signatures don't match.

- [ ] **Step 5.4: Implement `Ctx` + publish points + wiring**

In `state_machine.rs`, extend imports:

```rust
use crate::config::Config;            // (already present)
use crate::led::{self, LedState};
use tokio::sync::watch;
```

Add above `run_connection`:

```rust
/// Immutable per-connection context threaded through the event handlers (bundled to keep
/// the signatures within clippy's argument limit).
struct Ctx<'a> {
    cues: &'a Cues,
    cfg: &'a Config,
    led: &'a watch::Sender<LedState>,
}
```

In `run_connection`, after the button claim block:

```rust
    // LED is claimed per-connection like the button; guard drop (connection end/supersede)
    // aborts the render task, whose backend turns the light off on drop.
    let (led_tx, led_rx) = watch::channel(LedState::Idle);
    let _led_guard = led::spawn_led(&cfg.led, led_rx);
    let ctx = Ctx { cues: &cues, cfg: &cfg, led: &led_tx };
```

Update the two call sites in the select loop:

```rust
    // wake branch:
    start_turn(&mut wr, &mut mode, &ctx, &mut preroll).await?;
    // button branch:
    start_turn(&mut wr, &mut mode, &ctx, &mut preroll).await?;
    // hub branch:
    Some(Ok(e)) => handle_hub_event(e, &mut mode, detector.as_mut(), &mut wr, &mut playback, &ctx).await?,
```

New `start_turn` (replaces `cues` param with `ctx`; publishes `Listening`):

```rust
async fn start_turn<W: AsyncWrite + Unpin>(
    wr: &mut W, mode: &mut Mode, ctx: &Ctx<'_>, preroll: &mut VecDeque<Vec<i16>>,
) -> anyhow::Result<()> {
    write_event(wr, &WyomingEvent::new("run-pipeline")).await?;
    ctx.cues.play_awake();
    let _ = ctx.led.send(LedState::Listening);
    for chunk in preroll.drain(..) {
        write_event(wr, &WyomingEvent::audio_chunk(16000, 2, 1, to_pcm(&chunk))).await?;
    }
    *mode = Mode::Streaming;
    Ok(())
}
```

New `handle_hub_event` (replaces `cues`/`cfg` params with `ctx`; publishes on the three transitions):

```rust
async fn handle_hub_event<W: AsyncWrite + Unpin>(
    e: WyomingEvent,
    mode: &mut Mode,
    detector: Option<&mut WakeDetector>,
    wr: &mut W,
    playback: &mut Option<PlaybackSink>,
    ctx: &Ctx<'_>,
) -> anyhow::Result<()> {
    match e.event_type.as_str() {
        "run-satellite" => info!("run-satellite: armed"),
        "transcript" => {
            if *mode == Mode::Streaming {
                write_event(wr, &WyomingEvent::with_data("audio-stop", json!({"timestamp":0}))).await?;
                *mode = Mode::Idle;
                if let Some(d) = detector { d.reset(); }
                ctx.cues.play_done();
                let _ = ctx.led.send(LedState::Thinking);
            }
        }
        // Playback failures below are connection-fatal BY CHOICE: the hub redials and a fresh
        // connection re-arms everything; best-effort-continue would hide a dead audio device.
        "audio-start" => {
            if let Some(p) = playback.take() { p.kill().await; }
            *playback = Some(PlaybackSink::start(&ctx.cfg.snd_command)?);
            let _ = ctx.led.send(LedState::Speaking); // replies AND standalone announcements
        }
        "audio-chunk" => { if let Some(p) = playback.as_mut() { p.write_pcm(&e.payload).await?; } }
        "audio-stop" => {
            if let Some(p) = playback.take() { p.finish().await?; }
            let _ = ctx.led.send(LedState::Idle);
        }
        other => warn!("ignoring event {other}"),
    }
    Ok(())
}
```

- [ ] **Step 5.5: Run the satellite test suite**

Run: `cargo test`
Expected: PASS — all pre-existing tests (with updated call sites) plus the 3 new LED-publication tests. (`spike_wake` is slow in debug; if it drags, `cargo test --release` matches the parent plan's convention.)

- [ ] **Step 5.6: Commit**

```bash
git add satellite/src/satellite/state_machine.rs
git commit -m "feat(satellite): state machine publishes LedState; per-connection LED render task wiring"
```

---

### Task 6: Deployment + docs

**Files:**
- Modify: `satellite/deploy/nabu-satellite.service`
- Modify: `satellite/README.md`
- Modify: `docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md`

- [ ] **Step 6.1: Service unit**

In `nabu-satellite.service`, replace the `SupplementaryGroups` line and its comment:

```ini
# GPIO button/LED need the `gpio` group; a USB/evdev foot-switch needs `input`;
# the HAT's APA102 LEDs need `spi` (spidev nodes are root:spi on Raspberry Pi OS).
SupplementaryGroups=gpio input spi
```

Extend the header comment (top of file) with one line:

```ini
# LED: defaults to the HAT's APA102s (needs dtparam=spi=on). On a Jabra/headless build pass
# --no-led, or --led-gpio <pin> for a wired indicator (BCM pin -> ~330 ohm -> LED -> GND).
```

- [ ] **Step 6.2: README**

Add a section to `satellite/README.md` after the **Build** section:

```markdown
## Status LED

The satellite lights an LED while a voice interaction is active (turn start → end of TTS
playback; announcements too). Default: the reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs via
`/dev/spidev0.1` — requires `dtparam=spi=on` in `/boot/firmware/config.txt` and the `spi`
group (the systemd unit already adds it). Alternatives: `--led-gpio <pin>` for a single wired
LED (BCM numbering, pin → ~330 Ω → LED → GND), or `--no-led`. Missing LED hardware is not an
error — the satellite logs one warning and runs without it.
```

Add `--no-led \` to the WSL dev-test command (after `--no-button \`) and `--no-led` to the docker binfmt verification command (after `--no-button`) — neither environment has `/dev/spidev` and the flag keeps logs clean.

- [ ] **Step 6.3: Cross-reference in the parent plan**

In `docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md`, append one line to the status paragraph (the one starting `**Milestones 0–5.2 are COMPLETE**`):

```markdown
**Addendum 2026-06-10:** LED support (activity light: HAT APA102s / GPIO) was added post-hoc — spec `docs/superpowers/specs/2026-06-10-satellite-led-design.md`, plan `docs/superpowers/plans/2026-06-10-satellite-led-support.md`. Its on-device validation joins Task 5.3.
```

- [ ] **Step 6.4: Commit**

```bash
git add satellite/deploy/nabu-satellite.service satellite/README.md docs/superpowers/plans/2026-06-09-rust-wyoming-satellite.md
git commit -m "docs(satellite): LED provisioning — spi group, dtparam=spi=on, --no-led examples"
```

---

### Task 7: Full verification

- [ ] **Step 7.1: Full test suite (release, matches parent-plan convention)**

Run: `cargo test --release`
Expected: ALL PASS — the 24 pre-existing tests (3 of them with updated call sites) + 14 new (3 config, 8 led, 3 state-machine publication). The gate is zero failures, not the exact count.

- [ ] **Step 7.2: Clippy**

Run: `cargo clippy --all-targets -- -D warnings`
Expected: clean. (The `Ctx` refactor exists precisely to stay under `too_many_arguments`.)

- [ ] **Step 7.3: Cross-build smoke**

Run: `scripts/build-release.sh`
Expected: static `aarch64-unknown-linux-musl` binary builds (rppal `spi` adds no native deps; this is a regression gate, not a new capability).

- [ ] **Step 7.4: Mark plan checkboxes and commit**

```bash
git add docs/superpowers/plans/2026-06-10-satellite-led-support.md
git commit -m "docs(plan): mark satellite LED support tasks complete"
```

---

## On-device validation (deferred to parent-plan Milestone 5 / Task 5.3 — blocked on hardware)

- HAT: LEDs light blue on wake/button, stay lit through the reply, go dark after playback; `--led-gpio` variant on a breadboard LED; crash-restart leaves no stuck light (init-clear); supersede (hub reconnect) turns the light off.
- Verify `dtparam=spi=on` + `spi` group provisioning steps as written in the README.
