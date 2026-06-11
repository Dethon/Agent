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

/// Where the activity LED lives. Optional hardware: init failure degrades to LED-less.
#[derive(Clone, Debug, PartialEq)]
pub enum LedConfig {
    None,
    /// The reSpeaker 2-Mic HAT's 3 onboard APA102 LEDs on SPI0 (/dev/spidev0.1).
    Spi,
    /// Single indicator LED on a free GPIO pin (BCM numbering), active-high.
    Gpio(u8),
}

#[derive(Clone)]
pub struct Config {
    pub listen: String,         // matches Satellites:<id>:Address port (default 10700)
    pub mic_command: String,
    pub snd_command: String,
    pub detector: DetectorConfig,
    pub wake_enabled: bool,     // --no-wake disables on-device wake (button-only operation)
    pub button: ButtonConfig,
    pub led: LedConfig,         // activity LED; default = none, --led-spi / --led-gpio opt in
    pub preroll_ms: u32,        // zero-lag: how much recent audio to flush to the hub on trigger
    pub wake_preroll_ms: u32,   // wake-path flush: detection-latency gap only, NOT the wake word
    pub awake_cue: bool,
    pub done_cue: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            listen: "0.0.0.0:10700".into(),
            // Defaults target a Jabra Speak2 (55/75) on USB, index-pinned to ALSA card 0 by
            // provisioning (options snd_usb_audio index=0) — the card NAME is model/variant-
            // dependent (75->J75, 55 MS->MS, 55 UC->UC), so plughw:0,0 is baked in instead.
            // 48 kHz native -> plughw resamples for BOTH. For a reSpeaker 2-Mic HAT pass
            // --mic-command/--snd-command with plughw:CARD=seeed2micvoicec,DEV=0 plus
            // --button-gpio 17 and --led-spi; see provisioning.
            // -F 20000 (20 ms period): without it arecord defaults to buffer/4 = 125 ms periods
            // and every mic sample reaches stdout up to 125 ms late — paid on the wake AND the
            // speech->STT path. The 500 ms capture buffer default is independent of -F.
            mic_command: "arecord -D plughw:0,0 -r 16000 -c 1 -f S16_LE -t raw -F 20000".into(),
            // --start-delay=100000 (µs): aplay's default start threshold is the FULL 500 ms
            // buffer, so a streamed reply isn't audible until 500 ms of audio has been
            // synthesized+delivered; start at 100 ms queued instead (buffer stays 500 ms for
            // underrun headroom). -F 50000 reads stdin in 50 ms periods so the first write
            // into the ALSA buffer happens sooner.
            snd_command: "aplay -D plughw:0,0 -r 22050 -c 1 -f S16_LE -t raw --start-delay=100000 -F 50000".into(),
            detector: DetectorConfig::default(),
            wake_enabled: true,
            button: ButtonConfig::None, // the Jabra's own buttons are Linux-unusable (HID telephony); --button-* opts in
            led: LedConfig::None, // no LED on a Jabra build; --led-spi (HAT APA102s) or --led-gpio opt in
            preroll_ms: 1000,
            wake_preroll_ms: 240, // covers the ~181 ms measured detection latency with margin
            awake_cue: true,
            done_cue: true,
        }
    }
}

impl Config {
    /// Flags: --listen --mic-command --snd-command --threshold --no-wake
    ///        --button-gpio <pin> | --button-evdev <device>:<keycode> | --no-button
    ///        --led-spi | --led-gpio <pin> | --no-led
    ///        --preroll-ms <ms> --wake-preroll-ms <ms> --no-awake-cue --no-done-cue
    pub fn from_args() -> anyhow::Result<Self> {
        Self::parse(pico_args::Arguments::from_env())
    }

    fn parse(mut pa: pico_args::Arguments) -> anyhow::Result<Self> {
        let mut c = Config::default();
        if let Some(v) = pa.opt_value_from_str::<_, String>("--listen")? { c.listen = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--mic-command")? { c.mic_command = v; }
        if let Some(v) = pa.opt_value_from_str::<_, String>("--snd-command")? { c.snd_command = v; }
        if let Some(v) = pa.opt_value_from_str::<_, f32>("--threshold")? { c.detector.threshold = v; }
        if let Some(v) = pa.opt_value_from_str::<_, u32>("--preroll-ms")? { c.preroll_ms = v; }
        if let Some(v) = pa.opt_value_from_str::<_, u32>("--wake-preroll-ms")? { c.wake_preroll_ms = v; }
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
        if pa.contains("--no-led") {
            c.led = LedConfig::None;
        } else if pa.contains("--led-spi") {
            c.led = LedConfig::Spi;
        } else if let Some(pin) = pa.opt_value_from_str::<_, u8>("--led-gpio")? {
            c.led = LedConfig::Gpio(pin);
        }
        let rest = pa.finish();
        anyhow::ensure!(rest.is_empty(), "unknown arguments: {rest:?}");
        Ok(c)
    }

    /// Number of 1280-sample (80 ms) chunks to retain in the pre-roll ring buffer.
    pub fn preroll_chunks(&self) -> usize {
        (self.preroll_ms as usize).div_ceil(80)
    }

    /// Chunks to keep when a WAKE trigger flushes the pre-roll: just the detection-latency
    /// gap after the wake word ends (~181 ms measured) — NOT the wake word itself, which
    /// would otherwise be transcribed and dispatched as the request.
    pub fn wake_preroll_chunks(&self) -> usize {
        (self.wake_preroll_ms as usize).div_ceil(80)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(v: &[&str]) -> pico_args::Arguments {
        pico_args::Arguments::from_vec(v.iter().map(std::ffi::OsString::from).collect())
    }

    #[test]
    fn led_defaults_to_none() {
        assert_eq!(Config::default().led, LedConfig::None);
    }

    #[test]
    fn led_spi_flag_parses() {
        let c = Config::parse(args(&["--led-spi"])).unwrap();
        assert_eq!(c.led, LedConfig::Spi);
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

    #[test]
    fn defaults_are_sane() {
        let c = Config::default();
        assert_eq!(c.listen, "0.0.0.0:10700");
        assert!(c.mic_command.contains("arecord"));
        assert!(c.mic_command.contains("plughw:0,0"));
        assert!(c.mic_command.contains("-F 20000"), "mic must pin a 20 ms period (default 125 ms delays every sample)");
        assert!(c.snd_command.contains("aplay"));
        assert!(c.snd_command.contains("plughw:0,0"));
        assert!(c.snd_command.contains("--start-delay=100000"), "playback must start at ~100 ms queued, not a full buffer");
        assert!(c.snd_command.contains("-F 50000"), "playback period 50 ms so the first writei lands sooner");
        assert_eq!(c.detector.threshold, 0.5);
        assert!(c.wake_enabled);
        assert_eq!(c.button, ButtonConfig::None);
        assert_eq!(c.preroll_ms, 1000);
        assert_eq!(c.preroll_chunks(), 13); // ceil(1000 / 80)
        assert_eq!(c.wake_preroll_ms, 240);
        assert_eq!(c.wake_preroll_chunks(), 3);
    }
}
