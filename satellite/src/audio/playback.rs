use tokio::io::AsyncWriteExt;
use tokio::process::{Child, ChildStdin, Command};

/// Wraps a playback command (e.g. `aplay -D <dev> -r 22050 -c 1 -f S16_LE -t raw`).
/// One PlaybackSink per TTS stream (audio-start .. audio-stop): start on audio-start,
/// write_pcm per audio-chunk, finish() on audio-stop (closes stdin so aplay drains+exits).
pub struct PlaybackSink {
    child: Child,
    stdin: Option<ChildStdin>,
}

impl PlaybackSink {
    pub fn start(snd_command: &str) -> anyhow::Result<Self> {
        let mut child = Command::new("sh")
            .arg("-c")
            .arg(snd_command)
            .stdin(std::process::Stdio::piped())
            .kill_on_drop(true)
            .spawn()?;
        let stdin = child.stdin.take();
        Ok(Self { child, stdin })
    }

    pub async fn write_pcm(&mut self, pcm: &[u8]) -> anyhow::Result<()> {
        if let Some(s) = self.stdin.as_mut() {
            s.write_all(pcm).await?;
        }
        Ok(())
    }

    /// Close stdin and wait for the player to drain and exit.
    pub async fn finish(mut self) -> anyhow::Result<()> {
        drop(self.stdin.take()); // EOF on stdin -> aplay finishes
        let _ = self.child.wait().await?;
        Ok(())
    }

    /// Kill immediately (used if a new stream preempts an in-flight one).
    pub async fn kill(mut self) {
        let _ = self.child.kill().await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn accepts_a_playback_stream() {
        // `cat` consumes stdin and exits when closed — stands in for aplay.
        let mut sink = PlaybackSink::start("cat >/dev/null").unwrap();
        sink.write_pcm(&vec![0u8; 4410]).await.unwrap();
        sink.write_pcm(&vec![0u8; 4410]).await.unwrap();
        sink.finish().await.unwrap();
    }
}
