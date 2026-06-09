use crate::config::ButtonConfig;
use std::path::Path;
use std::time::Duration;
use tokio::sync::mpsc;
use tracing::{info, warn};

/// Keeps the button source alive for the life of the connection (drop = release pin / stop task).
pub enum ButtonGuard {
    // The pin is RAII-only: held so the line + async interrupt stay claimed, never read.
    Gpio(#[allow(dead_code)] rppal::gpio::InputPin),
    Evdev(tokio::task::JoinHandle<()>),
}

impl Drop for ButtonGuard {
    fn drop(&mut self) {
        if let ButtonGuard::Evdev(h) = self { h.abort(); }
    }
}

/// Build the configured button source. Ok(None) when no button is configured.
/// Debounced presses arrive as `()` on the receiver.
pub fn spawn_button(cfg: &ButtonConfig) -> anyhow::Result<Option<(ButtonGuard, mpsc::Receiver<()>)>> {
    match cfg {
        ButtonConfig::None => Ok(None),
        ButtonConfig::Gpio(pin) => Ok(Some(spawn_gpio(*pin)?)),
        ButtonConfig::Evdev { device, key } => Ok(Some(spawn_evdev(device, *key)?)),
    }
}

fn spawn_gpio(pin: u8) -> anyhow::Result<(ButtonGuard, mpsc::Receiver<()>)> {
    use rppal::gpio::{Gpio, Trigger};
    let (tx, rx) = mpsc::channel(8);
    // Input with pull-up: a press pulls the line low -> FallingEdge. 50 ms hardware debounce.
    let mut input = Gpio::new()?.get(pin)?.into_input_pullup();
    input.set_async_interrupt(
        Trigger::FallingEdge,
        Some(Duration::from_millis(50)),
        move |_event| { let _ = tx.try_send(()); },
    )?;
    info!("button: GPIO {pin} (falling edge, 50 ms debounce)");
    Ok((ButtonGuard::Gpio(input), rx))
}

fn spawn_evdev(device: &Path, key: u16) -> anyhow::Result<(ButtonGuard, mpsc::Receiver<()>)> {
    use evdev::{Device, EventSummary};
    let (tx, rx) = mpsc::channel(8);
    let dev = Device::open(device)?;
    let mut stream = dev.into_event_stream()?; // requires evdev "tokio" feature
    let want = key;
    let handle = tokio::spawn(async move {
        loop {
            match stream.next_event().await {
                // value 1 = key press (0 = release, 2 = autorepeat)
                Ok(ev) => if let EventSummary::Key(_, code, 1) = ev.destructure() {
                    if code.0 == want { let _ = tx.try_send(()); }
                },
                Err(e) => { warn!("evdev stream ended: {e}"); break; }
            }
        }
    });
    info!("button: evdev {} keycode {key}", device.display());
    Ok((ButtonGuard::Evdev(handle), rx))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::ButtonConfig;
    #[test]
    fn none_config_yields_no_button() {
        assert!(spawn_button(&ButtonConfig::None).unwrap().is_none());
    }
}
