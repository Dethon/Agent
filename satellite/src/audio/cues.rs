use crate::config::Config;

const AWAKE_WAV: &[u8] = include_bytes!("../../sounds/awake.wav");
const DONE_WAV: &[u8] = include_bytes!("../../sounds/done.wav");

/// Cue PCM decoded once at startup. Playback goes through the connection's playback pump
/// (PlaybackHandle::cue), which serializes cues with TTS streams on the single device owner —
/// a detached cue player can never race a reply's player for the exclusive ALSA device.
#[derive(Clone)]
pub struct Cues {
    awake_enabled: bool,
    done_enabled: bool,
    pub(crate) awake_pcm: Vec<u8>,
    pub(crate) done_pcm: Vec<u8>,
}

impl Cues {
    pub fn new(cfg: &Config) -> anyhow::Result<Self> {
        Ok(Self {
            awake_enabled: cfg.awake_cue,
            done_enabled: cfg.done_cue,
            awake_pcm: decode_wav_pcm(AWAKE_WAV)?,
            done_pcm: decode_wav_pcm(DONE_WAV)?,
        })
    }

    pub fn awake(&self) -> Option<Vec<u8>> {
        self.awake_enabled.then(|| self.awake_pcm.clone())
    }

    pub fn done(&self) -> Option<Vec<u8>> {
        self.done_enabled.then(|| self.done_pcm.clone())
    }
}

/// Require 22050 Hz mono 16-bit so the raw PCM matches the snd_command's `aplay -r 22050 -c 1 -f S16_LE`.
fn decode_wav_pcm(bytes: &[u8]) -> anyhow::Result<Vec<u8>> {
    let mut r = hound::WavReader::new(std::io::Cursor::new(bytes))?;
    let spec = r.spec();
    anyhow::ensure!(
        spec.sample_rate == 22050 && spec.channels == 1 && spec.bits_per_sample == 16,
        "cue WAV must be 22050 Hz mono 16-bit (got {} Hz, {} ch, {} bit)",
        spec.sample_rate, spec.channels, spec.bits_per_sample
    );
    let mut out = Vec::new();
    for s in r.samples::<i16>() {
        out.extend_from_slice(&s?.to_le_bytes());
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    #[test]
    fn decodes_cues_and_gates_on_flags() {
        let cues = Cues::new(&Config::default()).unwrap();
        assert!(!cues.awake().unwrap().is_empty());
        assert!(!cues.done().unwrap().is_empty());
        let off = Cues::new(&Config { awake_cue: false, done_cue: false, ..Config::default() }).unwrap();
        assert!(off.awake().is_none());
        assert!(off.done().is_none());
    }
}
