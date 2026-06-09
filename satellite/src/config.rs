use crate::wake::DetectorConfig;
use std::path::PathBuf;

/// Where the activation button comes from. Both impls live behind gpio::ButtonSource.
#[derive(Clone, Debug, PartialEq)]
pub enum ButtonConfig {
    None,
    /// Wired momentary button on a Pi GPIO pin (BCM numbering), via rppal.
    Gpio(u8),
    /// USB foot-switch / button that emits a key event, via the evdev crate.
    /// `key` is the numeric evdev keycode (e.g. 113 = KEY_MUTE, 28 = KEY_ENTER).
    Evdev { device: PathBuf, key: u16 },
}

#[derive(Clone)]
pub struct Config {
    pub listen: String,         // matches Satellites:<id>:Address port (default 10700)
    pub mic_command: String,
    pub snd_command: String,
    pub detector: DetectorConfig,
    pub wake_enabled: bool,     // --no-wake disables on-device wake (button-only operation)
    pub button: ButtonConfig,
    pub preroll_ms: u32,        // zero-lag: how much recent audio to flush to the hub on trigger
    pub awake_cue: bool,
    pub done_cue: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            listen: "0.0.0.0:10700".into(),
            // Defaults target the reSpeaker 2-Mic HAT. For a Jabra Speak, pass --mic-command/--snd-command
            // with plughw:CARD=<jabra-name>,DEV=0 for BOTH (48 kHz native -> ALSA resamples); see provisioning.
            mic_command: "arecord -D plughw:CARD=seeed2micvoicec,DEV=0 -r 16000 -c 1 -f S16_LE -t raw".into(),
            snd_command: "aplay -D plughw:CARD=seeed2micvoicec,DEV=0 -r 22050 -c 1 -f S16_LE -t raw".into(),
            detector: DetectorConfig::default(),
            wake_enabled: true,
            button: ButtonConfig::Gpio(17), // reSpeaker HAT onboard button; override with --button-* or --no-button
            preroll_ms: 1000,
            awake_cue: true,
            done_cue: true,
        }
    }
}

impl Config {
    /// Flags: --listen --mic-command --snd-command --threshold --no-wake
    ///        --button-gpio <pin> | --button-evdev <device>:<keycode> | --no-button
    ///        --preroll-ms <ms> --no-awake-cue --no-done-cue
    pub fn from_args() -> anyhow::Result<Self> {
        let mut pa = pico_args::Arguments::from_env();
        let mut c = Config::default();
        if let Some(v) = pa.opt_value_from_str::<_, String>("--listen")? { c.listen = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--mic-command")? { c.mic_command = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--snd-command")? { c.snd_command = v; }
        if let Some(v) = pa.opt_value_from_str::<_, f32>("--threshold")? { c.detector.threshold = v; }
        if let Some(v) = pa.opt_value_from_str::<_, u32>("--preroll-ms")? { c.preroll_ms = v; }
        if pa.contains("--no-wake") { c.wake_enabled = false; }
        if pa.contains("--no-awake-cue") { c.awake_cue = false; }
        if pa.contains("--no-done-cue") { c.done_cue = false; }
        if pa.contains("--no-button") {
            c.button = ButtonConfig::None;
        } else if let Some(pin) = pa.opt_value_from_str::<_, u8>("--button-gpio")? {
            c.button = ButtonConfig::Gpio(pin);
        } else if let Some(spec) = pa.opt_value_from_str::<_, String>("--button-evdev")? {
            let (dev, key) = spec.rsplit_once(':')
                .ok_or_else(|| anyhow::anyhow!("--button-evdev needs <device>:<keycode>, e.g. /dev/input/event3:28"))?;
            c.button = ButtonConfig::Evdev { device: dev.into(), key: key.parse()? };
        }
        let rest = pa.finish();
        anyhow::ensure!(rest.is_empty(), "unknown arguments: {rest:?}");
        Ok(c)
    }

    /// Number of 1280-sample (80 ms) chunks to retain in the pre-roll ring buffer.
    pub fn preroll_chunks(&self) -> usize {
        ((self.preroll_ms as usize) + 79) / 80
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn defaults_are_sane() {
        let c = Config::default();
        assert_eq!(c.listen, "0.0.0.0:10700");
        assert!(c.mic_command.contains("arecord"));
        assert!(c.snd_command.contains("aplay"));
        assert_eq!(c.detector.threshold, 0.5);
        assert!(c.wake_enabled);
        assert_eq!(c.button, ButtonConfig::Gpio(17));
        assert_eq!(c.preroll_ms, 1000);
        assert_eq!(c.preroll_chunks(), 13); // ceil(1000 / 80)
    }
}
