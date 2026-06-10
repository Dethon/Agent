//! Activity LED: the state machine publishes semantic LedState values on a watch channel;
//! a per-connection render task owns the hardware backend and maps states to light.
//! V1 policy: Idle -> off, everything else -> steady on.

use crate::config::LedConfig;
use tokio::sync::watch;
use tracing::warn;

/// Semantic satellite phase, published by the state machine. The render task — never the
/// state machine — decides what each phase looks like, so future blink patterns touch
/// only this module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LedState { Idle, Listening, Thinking, Speaking }

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

/// The hardware behind the light. Owned by the render task; dropped on connection end.
enum LedBackend {
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
            // Unlike the button (whose pin lives in the guard and releases synchronously on
            // supersede), this pin lives in the render task and releases when the aborted
            // task is dropped — a rapid hub reconnect can lose the race and run one
            // connection LED-less (warn below); it self-heals on the next reconnect.
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

/// Display fallback: if a reply never arrives after a transcript (hub error/timeout — a
/// known deferred race), stop glowing after the hub's own 120 s reply timeout.
/// The window restarts on any send (watch notifies per send); the state machine publishes
/// only on real transitions.
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

#[cfg(test)]
mod tests {
    use super::*;
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
        // Thinking and Speaking keep the light on -> the render task must observe each
        // state without writing again (yield generously so it actually gets to run).
        tx.send(LedState::Thinking).unwrap();
        for _ in 0..10 { tokio::task::yield_now().await; }
        assert_eq!(*log.lock().unwrap(), vec![true], "Thinking must not rewrite a lit LED");
        tx.send(LedState::Speaking).unwrap();
        for _ in 0..10 { tokio::task::yield_now().await; }
        assert_eq!(*log.lock().unwrap(), vec![true], "Speaking must not rewrite a lit LED");
        // Idle turns it off.
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
}
