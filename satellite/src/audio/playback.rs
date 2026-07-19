use tokio::io::AsyncWriteExt;
use tokio::process::{Child, ChildStdin};
use tokio::sync::mpsc;

/// Wraps a playback command (e.g. `aplay -D <dev> -r 22050 -c 1 -f S16_LE -t raw`).
/// One PlaybackSink per TTS stream (audio-start .. audio-stop): start on audio-start,
/// write_pcm per audio-chunk, finish() on audio-stop (closes stdin so aplay drains+exits).
pub struct PlaybackSink {
    child: Child,
    stdin: Option<ChildStdin>,
}

impl PlaybackSink {
    pub fn start(snd_command: &str) -> anyhow::Result<Self> {
        let mut child = crate::audio::build_command(snd_command)
            .stdin(std::process::Stdio::piped())
            // Discard aplay's stderr: it emits cosmetic `underrun!!!` xrun spam on a busy device
            // (notably an ALSA dmix unit, which consumes in ~one-period bursts and discards its
            // buffer on each xrun) that says nothing actionable. Real playback failures still
            // surface via spawn() errors, the child exit status (finish()), and EPIPE on write.
            .stderr(std::process::Stdio::null())
            .kill_on_drop(true)
            .spawn()?;
        let stdin = child.stdin.take();
        Ok(Self { child, stdin })
    }

    pub async fn write_pcm(&mut self, pcm: &[u8]) -> anyhow::Result<()> {
        if let Some(s) = self.stdin.as_mut() {
            s.write_all(pcm).await?;
        } else {
            tracing::warn!("write_pcm called after stdin closed; dropping {} bytes", pcm.len());
        }
        Ok(())
    }

    /// Close stdin and wait for the player to drain and exit.
    pub async fn finish(mut self) -> anyhow::Result<()> {
        drop(self.stdin.take()); // EOF on stdin -> aplay finishes
        let status = self.child.wait().await?;
        if !status.success() {
            tracing::warn!("playback command exited with {status}");
        }
        Ok(())
    }

    /// Kill immediately (used if a new stream preempts an in-flight one).
    pub async fn kill(mut self) {
        let _ = self.child.kill().await;
    }
}

/// Commands accepted by the playback pump — the single owner of the playback device.
pub enum PlaybackCmd {
    /// Begin a stream (kills a still-open previous stream: mid-stream preempt).
    Start { generation: u64 },
    Pcm(Vec<u8>),
    /// End the stream: close stdin, let the player drain, then report a DrainDone.
    Stop { generation: u64 },
    /// Best-effort short earcon: plays only when no stream is active; errors are non-fatal.
    Cue(Vec<u8>),
}

/// Completion report for a Stop — and the carrier for fatal playback errors.
pub struct DrainDone {
    pub generation: u64,
    pub result: anyhow::Result<()>,
}

/// Main-loop side of the pump. Stream sends await on the bounded channel, preserving the
/// flow control that writing into the player pipe used to provide; cues are fire-and-forget.
pub struct PlaybackHandle {
    cmd_tx: mpsc::Sender<PlaybackCmd>,
    generation: u64,
}

impl PlaybackHandle {
    pub async fn start(&mut self) -> anyhow::Result<()> {
        self.generation += 1;
        self.send(PlaybackCmd::Start { generation: self.generation }).await
    }

    pub async fn pcm(&self, pcm: Vec<u8>) -> anyhow::Result<()> {
        self.send(PlaybackCmd::Pcm(pcm)).await
    }

    pub async fn stop(&self) -> anyhow::Result<()> {
        self.send(PlaybackCmd::Stop { generation: self.generation }).await
    }

    /// try_send on purpose: when the pump is backlogged a late cue is worse than no cue.
    pub fn cue(&self, pcm: Vec<u8>) {
        let _ = self.cmd_tx.try_send(PlaybackCmd::Cue(pcm));
    }

    pub fn latest_generation(&self) -> u64 {
        self.generation
    }

    async fn send(&self, cmd: PlaybackCmd) -> anyhow::Result<()> {
        self.cmd_tx.send(cmd).await.map_err(|_| anyhow::anyhow!("playback pump terminated"))
    }
}

/// Spawn the playback pump. The drain after a Stop (≈0.5-2 s of buffered TTS on a Pi) happens
/// inside this task, so the connection's select! loop stays live for wake/button/mic the whole
/// time. The caller must abort-guard the JoinHandle so the pump (and its kill_on_drop player
/// child) dies with the connection.
pub fn spawn_pump(
    snd_command: &str,
) -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, tokio::task::JoinHandle<()>) {
    let (cmd_tx, cmd_rx) = mpsc::channel(16);
    // Unbounded ON PURPOSE: a bounded blocking send from the pump could AB-deadlock against a
    // main loop blocked sending a command. Completions are tiny and at most one per stream.
    let (done_tx, done_rx) = mpsc::unbounded_channel();
    let task = tokio::spawn(run_pump(snd_command.to_string(), cmd_rx, done_tx));
    (PlaybackHandle { cmd_tx, generation: 0 }, done_rx, task)
}

async fn run_pump(
    snd_command: String,
    mut cmd_rx: mpsc::Receiver<PlaybackCmd>,
    done_tx: mpsc::UnboundedSender<DrainDone>,
) {
    // A fresh sink per TTS stream, closed on Stop so the exact finish()/wait() drain reports
    // actual playback completion (the LED's Idle transition depends on it).
    let mut sink: Option<PlaybackSink> = None;
    let mut generation = 0u64;
    let mut streaming = false; // a TTS stream (audio-start .. audio-stop) is currently playing

    while let Some(cmd) = cmd_rx.recv().await {
        let result: anyhow::Result<()> = match cmd {
            PlaybackCmd::Start { generation: g } => {
                generation = g;
                streaming = true;
                if let Some(p) = sink.take() { p.kill().await; } // mid-stream preempt
                PlaybackSink::start(&snd_command).map(|p| sink = Some(p))
            }
            PlaybackCmd::Pcm(pcm) => match sink.as_mut() {
                Some(p) => p.write_pcm(&pcm).await,
                None => Ok(()), // stream already gone; drop the chunk
            },
            PlaybackCmd::Stop { generation: g } => {
                streaming = false;
                // Close the sink, let the player drain, report exact completion.
                let result = match sink.take() {
                    Some(p) => p.finish().await,
                    None => Ok(()),
                };
                let fatal = result.is_err();
                let _ = done_tx.send(DrainDone { generation: g, result });
                if fatal {
                    break;
                }
                continue;
            }
            PlaybackCmd::Cue(pcm) => {
                // Cues are dropped while a TTS stream is active.
                if !streaming {
                    if let Err(e) = play_cue(&snd_command, &pcm).await {
                        tracing::warn!("cue playback failed: {e:#}");
                    }
                }
                Ok(())
            }
        };
        if let Err(e) = result {
            let _ = done_tx.send(DrainDone { generation, result: Err(e) });
            break; // stop consuming; the dropped channels surface as fatal in the main loop
        }
    }
    // sink (if any) drops here -> kill_on_drop reaps the player child
}

async fn play_cue(snd_command: &str, pcm: &[u8]) -> anyhow::Result<()> {
    let mut p = PlaybackSink::start(snd_command)?;
    p.write_pcm(pcm).await?;
    p.finish().await
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

    #[tokio::test]
    async fn pump_reports_drain_done_with_stream_generation() {
        let (mut handle, mut done_rx, _task) = spawn_pump("cat >/dev/null");
        handle.start().await.unwrap();
        handle.pcm(vec![0u8; 4410]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert_eq!(d.generation, 1);
        assert!(d.result.is_ok());
    }

    // The exclusive-device guarantee: a cue and a stream queued back-to-back must be
    // serialized by the pump. `flock -n` stands in for the exclusive ALSA device — if the
    // stream's player spawned while the cue's player still lives, it exits immediately
    // (lock held) and the stream's writes fail, exactly like aplay's EBUSY -> EPIPE.
    #[tokio::test]
    async fn pump_serializes_cue_and_stream_on_an_exclusive_device() {
        let lock = std::env::temp_dir().join(format!("nabu-pump-test-{}.lock", std::process::id()));
        let snd = format!("flock -n {} -c 'cat >/dev/null'", lock.display());
        let (mut handle, mut done_rx, _task) = spawn_pump(&snd);
        handle.cue(vec![0u8; 8820]); // ~200 ms worth of 22050 Hz PCM
        handle.start().await.unwrap();
        handle.pcm(vec![0u8; 4410]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert!(d.result.is_ok(), "stream must not race the cue for the device: {:?}", d.result);
        let _ = std::fs::remove_file(lock);
    }

    #[tokio::test]
    async fn pump_playback_error_is_reported_fatally() {
        // player dies instantly (as aplay does on a busy/absent device) -> a later write EPIPEs
        let (mut handle, mut done_rx, _task) = spawn_pump("exit 1");
        handle.start().await.unwrap();
        let mut failed = false;
        for _ in 0..50 {
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            if handle.pcm(vec![0u8; 4410]).await.is_err() {
                failed = true; // pump already died and reported; channel closed
                break;
            }
        }
        let d = done_rx.recv().await;
        match d {
            Some(d) => assert!(d.result.is_err(), "dead player must surface as a fatal error"),
            None => assert!(failed, "pump ended without reporting an error"),
        }
    }

    // While idle (no stream, no cue) the pump must not open or feed a player at all — the
    // playback device stays free. `cat >> <file>` stands in for aplay: the file is never created.
    #[tokio::test]
    async fn idle_pump_leaves_the_device_untouched() {
        let path = std::env::temp_dir().join(format!("nabu-idle-{}.raw", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let snd = format!("cat >> {}", path.display());
        let (_handle, _done_rx, task) = spawn_pump(&snd);
        tokio::time::sleep(std::time::Duration::from_millis(150)).await;
        task.abort();
        let created = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        let _ = std::fs::remove_file(&path);
        assert_eq!(created, 0, "an idle pump must not open or feed a player");
    }
}
