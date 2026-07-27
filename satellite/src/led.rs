//! Activity LED: the state machine publishes semantic LedState values on a watch channel;
//! a per-connection render task owns the hardware backend and maps states to light.
//! The backend is the reSpeaker XVF3800's 12-LED WS2812 ring, driven over USB vendor
//! control transfers against device-control resource 0x14. The protocol was captured from
//! the vendor's xvf_host tool with usbmon — see
//! docs/superpowers/specs/2026-07-27-xvf3800-led-ring-design.md.

use crate::config::LedConfig;
use nusb::transfer::{ControlIn, ControlOut, ControlType, Recipient};
use nusb::MaybeFuture;
use tokio::sync::watch;
use tracing::{debug, warn};

/// Semantic satellite phase, published by the state machine. The render task — never the
/// state machine — decides what each phase looks like, so future blink patterns touch
/// only this module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LedState { Idle, Listening, Thinking, Speaking }

/// reSpeaker XVF3800 USB ids, and the device-control resource that owns the LED ring.
const XVF3800_VID: u16 = 0x2886;
const XVF3800_PID: u16 = 0x001a;
const RESID_LED: u16 = 0x14;

/// Device-control command ids on RESID_LED. Captured from the vendor's xvf_host with usbmon
/// and verified on the deployed unit; see the design spec.
const LED_EFFECT: u16 = 0x0c;
const LED_BRIGHTNESS: u16 = 0x0d;
const LED_SPEED: u16 = 0x0f;
const LED_COLOR: u16 = 0x10;
const LED_DOA_COLOR: u16 = 0x11;

/// LED_EFFECT modes (2 = rainbow, unused: it is the device's power-on animation).
const EFFECT_OFF: u8 = 0;
const EFFECT_BREATH: u8 = 1;
const EFFECT_SINGLE: u8 = 3;
const EFFECT_DOA: u8 = 4;

/// The look, tuned by eye against the deployed unit. Brightness is baked into the colour value
/// in single-colour and DoA modes, which is why those are pre-dimmed; breath instead MULTIPLIES
/// its colour by LED_BRIGHTNESS, so THINKING_COLOR must stay saturated — a pre-dimmed value
/// there renders as no visible pulse at all (measured: 0x002040 at brightness 127 is invisible).
const DOA_BASE_COLOR: u32 = 0x00_2040;     // dim blue ring
const DOA_POINTER_COLOR: u32 = 0x00_C066;  // green direction-of-arrival pointer
const THINKING_COLOR: u32 = 0x00_00FF;     // breathing blue, saturated (see above)
const SPEAKING_COLOR: u32 = 0x00_40A0;     // solid blue
const BREATH_BRIGHTNESS: u8 = 255;
// Slowest animating value: LED_SPEED is a rate, and 0 stops the animation outright. Even 2
// read as too brisk for a "thinking" pulse on the device.
const BREATH_SPEED: u8 = 1;

/// Transfers to this device complete in ~250 µs; the timeout only bounds a wedged device.
const CTRL_TIMEOUT: std::time::Duration = std::time::Duration::from_millis(200);

/// One device-control write: a command id plus its little-endian payload. 8 bytes is the
/// widest payload (LED_DOA_COLOR's two u32s), so it lives inline — no allocation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct LedCmd { cmd: u16, len: usize, buf: [u8; 8] }

impl LedCmd {
    const fn u8v(cmd: u16, v: u8) -> Self {
        Self { cmd, len: 1, buf: [v, 0, 0, 0, 0, 0, 0, 0] }
    }
    const fn u32v(cmd: u16, v: u32) -> Self {
        let b = v.to_le_bytes();
        Self { cmd, len: 4, buf: [b[0], b[1], b[2], b[3], 0, 0, 0, 0] }
    }
    const fn u32x2(cmd: u16, a: u32, b: u32) -> Self {
        let (x, y) = (a.to_le_bytes(), b.to_le_bytes());
        Self { cmd, len: 8, buf: [x[0], x[1], x[2], x[3], y[0], y[1], y[2], y[3]] }
    }
    fn payload(&self) -> &[u8] { &self.buf[..self.len] }
    /// The u32 a LED_COLOR write carries — the render loop's redundant-write cache compares it.
    fn as_u32(&self) -> u32 {
        u32::from_le_bytes([self.buf[0], self.buf[1], self.buf[2], self.buf[3]])
    }
}

/// Run once when the backend is built: pin the breath and DoA look so it can't drift from a
/// firmware reflash or another tool, then clear the ring's power-on rainbow/DoA state.
const INIT: [LedCmd; 4] = [
    LedCmd::u8v(LED_BRIGHTNESS, BREATH_BRIGHTNESS),
    LedCmd::u8v(LED_SPEED, BREATH_SPEED),
    LedCmd::u32x2(LED_DOA_COLOR, DOA_BASE_COLOR, DOA_POINTER_COLOR),
    LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
];

const IDLE: [LedCmd; 1] = [LedCmd::u8v(LED_EFFECT, EFFECT_OFF)];
const LISTENING: [LedCmd; 1] = [LedCmd::u8v(LED_EFFECT, EFFECT_DOA)];
// Colour before effect, so the previous colour never flashes in the new mode.
const THINKING: [LedCmd; 2] = [
    LedCmd::u32v(LED_COLOR, THINKING_COLOR),
    LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
];
const SPEAKING: [LedCmd; 2] = [
    LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
    LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
];

/// What each semantic phase looks like. The render task — never the state machine — decides.
fn commands_for(state: LedState) -> &'static [LedCmd] {
    match state {
        LedState::Idle => &IDLE,
        LedState::Listening => &LISTENING,
        LedState::Thinking => &THINKING,
        LedState::Speaking => &SPEAKING,
    }
}

/// The hardware behind the light. Owned by the render task; dropped on connection end.
enum LedBackend {
    /// The XVF3800's LED ring, addressed over USB control transfers.
    Xvf3800(nusb::Device),
    #[cfg(test)]
    Probe(std::sync::Arc<std::sync::Mutex<Vec<LedCmd>>>),
}

impl LedBackend {
    /// One device-control write, followed by the status read xvf_host itself performs.
    /// Without the status read a rejected command or a wrong id would fail silently, and
    /// the write-failure policy below would never trigger.
    fn write(&mut self, c: &LedCmd) -> anyhow::Result<()> {
        match self {
            LedBackend::Xvf3800(dev) => {
                dev.control_out(ControlOut {
                    control_type: ControlType::Vendor,
                    recipient: Recipient::Device,
                    request: 0x00,
                    value: c.cmd,
                    index: RESID_LED,
                    data: c.payload(),
                }, CTRL_TIMEOUT).wait()?;
                let status = dev.control_in(ControlIn {
                    control_type: ControlType::Vendor,
                    recipient: Recipient::Device,
                    request: 0x00,
                    value: c.cmd,
                    index: RESID_LED,
                    length: 1,
                }, CTRL_TIMEOUT).wait()?;
                anyhow::ensure!(status.first() == Some(&0),
                    "led command {:#06x} rejected: status {status:?}", c.cmd);
                Ok(())
            }
            #[cfg(test)]
            LedBackend::Probe(log) => { log.lock().unwrap().push(*c); Ok(()) }
        }
    }
}

impl Drop for LedBackend {
    // The ring latches its last state, so a dropped backend must blank it explicitly. This
    // runs on task abort (connection end/supersede), where render_loop's own exit blank
    // never gets to run. Probe is exempt so tests observe only deliberate writes.
    fn drop(&mut self) {
        if matches!(self, LedBackend::Xvf3800(_)) {
            let _ = self.write(&LedCmd::u8v(LED_EFFECT, EFFECT_OFF));
        }
    }
}

/// Ok(None) when no LED is configured or no ring is present. Errors bubble to spawn_led,
/// which warns and runs LED-less — missing hardware must never take the satellite down.
fn build_backend(cfg: &LedConfig) -> anyhow::Result<Option<LedBackend>> {
    match cfg {
        LedConfig::None => Ok(None),
        LedConfig::Auto => {
            // A host with no USB subsystem at all (the WSL dev host has no
            // /sys/bus/usb/devices) is the same class of "not present" as an empty bus —
            // neither is worth a warning, or every dev-satellite start would emit one.
            let Ok(mut devices) = nusb::list_devices().wait() else {
                debug!("no USB subsystem; running without an LED");
                return Ok(None);
            };
            let Some(info) = devices
                .find(|d| d.vendor_id() == XVF3800_VID && d.product_id() == XVF3800_PID)
            else {
                debug!("no reSpeaker XVF3800 on USB; running without an LED");
                return Ok(None);
            };
            // A ring that IS present but won't open is actionable (missing udev rule /
            // permissions), so this error propagates to spawn_led's warning.
            let mut backend = LedBackend::Xvf3800(info.open().wait()?);
            for c in INIT.iter() { backend.write(c)?; }
            Ok(Some(backend))
        }
    }
}

/// Display fallback: if a reply never arrives after voice-stopped (hub error/timeout — a
/// known deferred race), stop glowing after the hub's own 120 s reply timeout. Keyed on the
/// state being Thinking, not on which event set it, so it covers any future trigger too.
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

/// Write the phase's command sequence, skipping a LED_COLOR that already holds this value.
/// The effect write is never skipped: re-entering Thinking after Idle must switch the mode
/// back even though the colour is unchanged.
fn apply(backend: &mut LedBackend, state: LedState, last_color: &mut Option<u32>) -> anyhow::Result<()> {
    for c in commands_for(state) {
        if c.cmd == LED_COLOR && *last_color == Some(c.as_u32()) { continue; }
        backend.write(c)?;
        if c.cmd == LED_COLOR { *last_color = Some(c.as_u32()); }
    }
    Ok(())
}

/// Applies each phase's look, writing only on transitions. A write failure disables the LED
/// for the rest of the connection (one warning, no spam); the next connection re-initializes.
/// LED problems never tear down a connection.
async fn render_loop(mut rx: watch::Receiver<LedState>, mut backend: LedBackend) {
    // The backend's INIT already blanked the ring and the initial state is Idle, so start
    // in sync — otherwise every connection would open with a redundant blank.
    let mut shown = Some(LedState::Idle);
    let mut last_color: Option<u32> = None;
    loop {
        let state = *rx.borrow_and_update();
        if shown != Some(state) {
            if let Err(e) = apply(&mut backend, state, &mut last_color) {
                warn!("led write failed, led disabled for this connection: {e:#}");
                return;
            }
            shown = Some(state);
        }
        let changed = if state == LedState::Thinking {
            match tokio::time::timeout(THINKING_FALLBACK, rx.changed()).await {
                Err(_elapsed) => {
                    if shown != Some(LedState::Idle) {
                        if let Err(e) = apply(&mut backend, LedState::Idle, &mut last_color) {
                            warn!("led write failed, led disabled for this connection: {e:#}");
                            return;
                        }
                        shown = Some(LedState::Idle);
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
    if shown != Some(LedState::Idle) {
        let _ = apply(&mut backend, LedState::Idle, &mut last_color);
    }
}

/// Best-effort one-shot blank, used at process start (the ring's power-on default is a
/// rainbow then DoA — lit, with no hub connected) and at graceful shutdown (so a stopped
/// service does not leave it lit). Building the backend runs INIT, which ends in a blank,
/// and dropping it blanks again; both are idempotent. No-op under --no-led: that flag means
/// this process never touches the ring.
pub fn blank_once(cfg: &LedConfig) {
    if matches!(cfg, LedConfig::None) { return; }
    if let Err(e) = build_backend(cfg) {
        debug!("led blank skipped: {e:#}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::LedConfig;
    use std::sync::{Arc, Mutex};

    fn probe() -> (Arc<Mutex<Vec<LedCmd>>>, LedBackend) {
        let log = Arc::new(Mutex::new(Vec::new()));
        (log.clone(), LedBackend::Probe(log))
    }

    #[test]
    fn probe_backend_records_writes() {
        let (log, mut b) = probe();
        b.write(&LedCmd::u8v(LED_EFFECT, EFFECT_DOA)).unwrap();
        assert_eq!(*log.lock().unwrap(), vec![LedCmd::u8v(LED_EFFECT, EFFECT_DOA)]);
    }

    #[test]
    fn none_config_yields_no_backend() {
        assert!(build_backend(&LedConfig::None).unwrap().is_none());
    }

    // A redundant LED_COLOR is skipped, but the effect write is not: re-entering Thinking
    // after Idle must still switch the mode back to breath.
    #[test]
    fn apply_skips_a_redundant_colour_but_never_the_effect() {
        let (log, mut b) = probe();
        let mut last = None;
        apply(&mut b, LedState::Thinking, &mut last).unwrap();
        apply(&mut b, LedState::Idle, &mut last).unwrap();
        apply(&mut b, LedState::Thinking, &mut last).unwrap();
        assert_eq!(*log.lock().unwrap(), vec![
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
            LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
        ]);
    }

    use tokio::sync::watch;

    // Poll-with-yield instead of sleeping: these tests run under start_paused, where
    // yield_now keeps the runtime busy (no auto-advance) while the render task catches up.
    async fn wait_probe(log: &Arc<Mutex<Vec<LedCmd>>>, expect: &[LedCmd]) {
        for _ in 0..100 {
            if log.lock().unwrap().as_slice() == expect { return; }
            tokio::task::yield_now().await;
        }
        panic!("probe never reached {expect:?}, got {:?}", log.lock().unwrap());
    }

    #[tokio::test(start_paused = true)]
    async fn render_applies_each_phase_and_writes_only_on_change() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let _task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Listening).unwrap();
        wait_probe(&log, &[LedCmd::u8v(LED_EFFECT, EFFECT_DOA)]).await;
        // Re-sending the same state must not rewrite it (watch notifies per send).
        tx.send(LedState::Listening).unwrap();
        for _ in 0..10 { tokio::task::yield_now().await; }
        assert_eq!(log.lock().unwrap().len(), 1, "an unchanged state must not rewrite");
        tx.send(LedState::Thinking).unwrap();
        wait_probe(&log, &[
            LedCmd::u8v(LED_EFFECT, EFFECT_DOA),
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
        ]).await;
        tx.send(LedState::Speaking).unwrap();
        wait_probe(&log, &[
            LedCmd::u8v(LED_EFFECT, EFFECT_DOA),
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
            LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
        ]).await;
    }

    // The initial state is Idle and the backend's INIT already blanked the ring, so the
    // render task must not open with a redundant blank.
    #[tokio::test(start_paused = true)]
    async fn render_does_not_rewrite_the_initial_idle() {
        let (log, backend) = probe();
        let (_tx, rx) = watch::channel(LedState::Idle);
        let _task = tokio::spawn(render_loop(rx, backend));
        for _ in 0..10 { tokio::task::yield_now().await; }
        assert!(log.lock().unwrap().is_empty(), "initial Idle must write nothing");
    }

    #[tokio::test(start_paused = true)]
    async fn thinking_goes_dark_after_fallback_and_relights_on_late_reply() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let _task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Thinking).unwrap();
        // When wait_probe sees the writes, the render task has already polled (and thus
        // registered) the timeout future — the writes and the await are one synchronous stretch.
        wait_probe(&log, &[
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
        ]).await;
        tokio::time::advance(THINKING_FALLBACK + std::time::Duration::from_secs(1)).await;
        wait_probe(&log, &[
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
            LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
        ]).await;
        tx.send(LedState::Speaking).unwrap(); // late reply still lights up
        wait_probe(&log, &[
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
            LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
            LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
        ]).await;
    }

    #[tokio::test(start_paused = true)]
    async fn sender_drop_blanks_the_ring_and_ends_task() {
        let (log, backend) = probe();
        let (tx, rx) = watch::channel(LedState::Idle);
        let task = tokio::spawn(render_loop(rx, backend));
        tx.send(LedState::Speaking).unwrap();
        wait_probe(&log, &[
            LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
        ]).await;
        drop(tx); // connection ending
        task.await.unwrap();
        assert_eq!(*log.lock().unwrap(), vec![
            LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
            LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
        ]);
    }

    #[tokio::test]
    async fn none_config_spawns_no_task() {
        let (_tx, rx) = watch::channel(LedState::Idle);
        assert!(spawn_led(&LedConfig::None, rx).is_none());
    }

    // --no-led means this process never touches the ring, so the one-shot blank is a no-op.
    #[test]
    fn blank_once_is_a_noop_under_no_led() {
        blank_once(&LedConfig::None);
    }

    // Golden bytes, pinned against a usbmon capture of the vendor's xvf_host: a LED_COLOR
    // write of 0x2040 puts 40 20 00 00 on the wire (little-endian u32).
    #[test]
    fn led_cmd_encodes_little_endian_payloads() {
        assert_eq!(LedCmd::u8v(LED_EFFECT, 4).payload(), &[0x04]);
        assert_eq!(LedCmd::u32v(LED_COLOR, 0x2040).payload(), &[0x40, 0x20, 0x00, 0x00]);
        assert_eq!(
            LedCmd::u32x2(LED_DOA_COLOR, 0x002040, 0x00C066).payload(),
            &[0x40, 0x20, 0x00, 0x00, 0x66, 0xC0, 0x00, 0x00]
        );
    }

    #[test]
    fn led_cmd_as_u32_round_trips() {
        assert_eq!(LedCmd::u32v(LED_COLOR, 0x0040A0).as_u32(), 0x0040A0);
    }

    // Idle is dark; Listening is the device's DoA mode (blue ring, green pointer).
    #[test]
    fn idle_and_listening_are_single_effect_writes() {
        assert_eq!(commands_for(LedState::Idle), &[LedCmd::u8v(LED_EFFECT, EFFECT_OFF)]);
        assert_eq!(commands_for(LedState::Listening), &[LedCmd::u8v(LED_EFFECT, EFFECT_DOA)]);
    }

    // Colour MUST precede effect: writing the effect first would flash the previous colour
    // in the new mode for one transfer.
    #[test]
    fn thinking_and_speaking_write_colour_before_effect() {
        assert_eq!(commands_for(LedState::Thinking), &[
            LedCmd::u32v(LED_COLOR, THINKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_BREATH),
        ]);
        assert_eq!(commands_for(LedState::Speaking), &[
            LedCmd::u32v(LED_COLOR, SPEAKING_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_SINGLE),
        ]);
    }

    // Init pins the look so it can't drift from a reflash or another tool, then clears the
    // device's power-on rainbow/DoA state — the blank must be LAST.
    #[test]
    fn init_pins_the_look_then_blanks() {
        assert_eq!(INIT, [
            LedCmd::u8v(LED_BRIGHTNESS, BREATH_BRIGHTNESS),
            LedCmd::u8v(LED_SPEED, BREATH_SPEED),
            LedCmd::u32x2(LED_DOA_COLOR, DOA_BASE_COLOR, DOA_POINTER_COLOR),
            LedCmd::u8v(LED_EFFECT, EFFECT_OFF),
        ]);
    }
}
