use crate::audio::playback::PlaybackSink;
use crate::config::Config;

const AWAKE_WAV: &[u8] = include_bytes!("../../sounds/awake.wav");
const DONE_WAV: &[u8] = include_bytes!("../../sounds/done.wav");

/// Decoded once at startup; each play spawns a short-lived aplay sink so it never blocks the loop.
#[derive(Clone)]
pub struct Cues {
    snd_command: String,
    awake_enabled: bool,
    done_enabled: bool,
    pub(crate) awake_pcm: Vec<u8>,
    pub(crate) done_pcm: Vec<u8>,
}

impl Cues {
    pub fn new(cfg: &Config) -> anyhow::Result<Self> {
        Ok(Self {
            snd_command: cfg.snd_command.clone(),
            awake_enabled: cfg.awake_cue,
            done_enabled: cfg.done_cue,
            awake_pcm: decode_wav_pcm(AWAKE_WAV)?,
            done_pcm: decode_wav_pcm(DONE_WAV)?,
        })
    }
    pub fn play_awake(&self) { if self.awake_enabled { self.play(self.awake_pcm.clone()); } }
    pub fn play_done(&self) { if self.done_enabled { self.play(self.done_pcm.clone()); } }

    fn play(&self, pcm: Vec<u8>) {
        let cmd = self.snd_command.clone();
        tokio::spawn(async move {
            match PlaybackSink::start(&cmd) {
                Ok(mut sink) => {
                    if let Err(e) = sink.write_pcm(&pcm).await {
                        tracing::warn!("cue playback write failed: {e}");
                    } else if let Err(e) = sink.finish().await {
                        tracing::warn!("cue playback finish failed: {e}");
                    }
                }
                Err(e) => tracing::warn!("cue playback failed: {e}"),
            }
        });
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
    fn test_cfg() -> Config {
        Config { snd_command: "cat >/dev/null".into(), ..Config::default() }
    }
    #[tokio::test]
    async fn decodes_and_plays_cues() {
        let cues = Cues::new(&test_cfg()).unwrap();
        assert!(!cues.awake_pcm.is_empty());
        assert!(!cues.done_pcm.is_empty());
        cues.play_awake();
        cues.play_done();
        // give the spawned sink tasks a moment to run without panicking
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
}
