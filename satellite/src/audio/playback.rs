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
    keep_warm: bool,
) -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, tokio::task::JoinHandle<()>) {
    let (cmd_tx, cmd_rx) = mpsc::channel(16);
    // Unbounded ON PURPOSE: a bounded blocking send from the pump could AB-deadlock against a
    // main loop blocked sending a command. Completions are tiny and at most one per stream.
    let (done_tx, done_rx) = mpsc::unbounded_channel();
    let task = tokio::spawn(run_pump(snd_command.to_string(), cmd_rx, done_tx, keep_warm));
    (PlaybackHandle { cmd_tx, generation: 0 }, done_rx, task)
}

/// One 20 ms chunk (441 samples @ 22050 Hz mono S16LE) of digital silence, fed one-per-tick to
/// the keep-warm stream. 20 ms == IDLE_TICK, so the stream is topped up at exactly playback rate:
/// the buffered-ahead silence stays near aplay's start-delay (~100 ms), so audio injected into it
/// plays at the SAME latency a fresh sink has today — no added onset delay — yet the stream never
/// starves (which would xrun and re-cool the DAC).
const IDLE_SILENCE: [u8; 441 * 2] = [0u8; 441 * 2];
const IDLE_TICK: std::time::Duration = std::time::Duration::from_millis(20);
/// Playback byte rate (22050 Hz * 2 bytes mono S16LE) and the assumed buffered-ahead at a stream
/// start (~aplay start-delay), used only to ESTIMATE when injected audio finishes for the LED
/// drain signal. Precision is non-critical: on the Jabra (keep-warm's only user) there is no LED,
/// and the satellite reports nothing back to the hub on drain — LED devices run --no-keep-warm and
/// keep the exact finish()/wait() drain on the non-keep-warm path below.
const RATE_BPS: f64 = 22_050.0 * 2.0;
const IDLE_TARGET_BYTES: f64 = 4410.0; // ~100 ms

/// Best-effort open of the keep-warm stream. A failure is non-fatal: keep-warm just turns off for
/// this connection (cues/replies fall back to fresh sinks, exactly as before).
fn open_idle(snd_command: &str) -> Option<PlaybackSink> {
    match PlaybackSink::start(snd_command) {
        Ok(p) => Some(p),
        Err(e) => {
            tracing::warn!("keep-warm sink failed to open: {e:#}; audio may glitch on a cold DAC");
            None
        }
    }
}

/// Wait for the next command. While a keep-warm stream is open and no TTS stream is injecting,
/// top it up with one IDLE_SILENCE chunk per tick so the Jabra's DAC stays warm and the
/// buffered-ahead stays shallow. The silence write runs in the tick branch BODY — not as a raced
/// future — so it is never cancelled mid-write; only the cancellation-safe `recv()`/`tick()` are
/// raced. A dead keep-warm stream just disables warming for the rest of the connection.
async fn next_command(
    idle: &mut Option<PlaybackSink>,
    streaming: bool,
    cmd_rx: &mut mpsc::Receiver<PlaybackCmd>,
    tick: &mut tokio::time::Interval,
) -> Option<PlaybackCmd> {
    loop {
        // During a TTS stream the reply audio is what keeps the stream fed; don't interleave silence.
        if idle.is_none() || streaming {
            return cmd_rx.recv().await;
        }
        tokio::select! {
            biased; // a real command always wins over a silence top-up, so beeps aren't delayed
            cmd = cmd_rx.recv() => return cmd,
            _ = tick.tick() => {
                let died = match idle.as_mut() {
                    Some(p) => p.write_pcm(&IDLE_SILENCE).await.is_err(),
                    None => false,
                };
                if died { *idle = None; }
            }
        }
    }
}

async fn run_pump(
    snd_command: String,
    mut cmd_rx: mpsc::Receiver<PlaybackCmd>,
    done_tx: mpsc::UnboundedSender<DrainDone>,
    keep_warm: bool,
) {
    // Non-keep-warm path: a fresh sink per TTS stream with the precise finish()/wait() drain.
    let mut sink: Option<PlaybackSink> = None;
    // Keep-warm stream: ONE player held open for the whole connection. While idle it is fed paced
    // silence (DAC stays warm); cues AND TTS replies are INJECTED into this running stream so they
    // play with a clean onset. A fresh per-stream sink would re-trigger the Jabra's cold-start
    // glitch even on a warm DAC — warmth does NOT survive a session close+reopen — so nothing
    // opens a fresh sink while this stream is alive.
    let mut idle: Option<PlaybackSink> = if keep_warm { open_idle(&snd_command) } else { None };
    let mut generation = 0u64;
    let mut streaming = false; // a TTS stream (audio-start .. audio-stop) is currently injecting
    let mut stream_start = std::time::Instant::now(); // keep-warm drain estimate: anchor + bytes
    let mut stream_bytes = 0u64;
    let mut tick = tokio::time::interval(IDLE_TICK);
    // Resume feeding without a catch-up burst after a TTS stream (which leaves the interval idle).
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

    while let Some(cmd) = next_command(&mut idle, streaming, &mut cmd_rx, &mut tick).await {
        let result: anyhow::Result<()> = match cmd {
            PlaybackCmd::Start { generation: g } => {
                generation = g;
                streaming = true;
                if idle.is_some() {
                    // KEEP-WARM: inject the reply into the running stream — clean onset, no fresh open.
                    stream_start = std::time::Instant::now();
                    stream_bytes = 0;
                    Ok(())
                } else {
                    // Non-keep-warm: fresh sink (may glitch onset on a cold DAC), precise drain.
                    if let Some(p) = sink.take() { p.kill().await; } // mid-stream preempt
                    PlaybackSink::start(&snd_command).map(|p| sink = Some(p))
                }
            }
            PlaybackCmd::Pcm(pcm) => {
                if let Some(p) = idle.as_mut() {
                    stream_bytes += pcm.len() as u64;
                    let r = p.write_pcm(&pcm).await;
                    if r.is_err() { idle = None; } // dead keep-warm stream -> surfaces fatal below
                    r
                } else {
                    match sink.as_mut() {
                        Some(p) => p.write_pcm(&pcm).await,
                        None => Ok(()), // stream already gone; drop the chunk
                    }
                }
            }
            PlaybackCmd::Stop { generation: g } => {
                streaming = false;
                if idle.is_some() {
                    // KEEP-WARM: the stream stays open. Estimate when the injected reply finishes
                    // playing and fire DrainDone then (drives only the LED, a no-op on the Jabra).
                    let buffered =
                        IDLE_TARGET_BYTES + stream_bytes as f64 - stream_start.elapsed().as_secs_f64() * RATE_BPS;
                    let delay = std::time::Duration::from_secs_f64((buffered / RATE_BPS).max(0.0));
                    let tx = done_tx.clone();
                    tokio::spawn(async move {
                        tokio::time::sleep(delay).await;
                        let _ = tx.send(DrainDone { generation: g, result: Ok(()) });
                    });
                    continue;
                }
                // Non-keep-warm: close the sink, let aplay drain, report exact completion.
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
                // Cues are dropped while a TTS stream is active (as before).
                if !streaming {
                    match idle.as_mut() {
                        // Inject into the warm running stream -> clean onset, no fresh-open glitch.
                        Some(p) => {
                            if let Err(e) = p.write_pcm(&pcm).await {
                                tracing::warn!("cue inject failed: {e:#}");
                                idle = None;
                            }
                        }
                        // No keep-warm stream (off or died): fresh cue sink, may glitch on a cold DAC.
                        None => {
                            if let Err(e) = play_cue(&snd_command, &pcm).await {
                                tracing::warn!("cue playback failed: {e:#}");
                            }
                        }
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
    // sink / idle (if any) drop here -> kill_on_drop reaps the player children
}

async fn play_cue(snd_command: &str, pcm: &[u8]) -> anyhow::Result<()> {
    // No leading-silence prime: keep-warm holds the DAC warm via an idle silence stream, so a
    // fresh cue sink opens onto a warm DAC and plays clean with no added latency. (When keep-warm
    // is off the first chunk can still glitch on a cold open — that is the keep-warm trade-off,
    // not something a per-cue prime should pay for.)
    let mut p = PlaybackSink::start(snd_command)?;
    p.write_pcm(pcm).await?;
    p.finish().await
}

/// Play a brief `ms` tone through the playback device to wake a firmware-sleeping mic, then
/// drain and exit. The Jabra Speak2's capture ADC powers down when idle and, from deep sleep
/// (e.g. after the host reboots), ignores BOTH capture opens AND *silent* playback — but it
/// wakes on real audio signal. So we emit an actual tone (not zeros) before opening the mic.
/// 22050 Hz mono S16LE matches the satellite's fixed playback format.
pub async fn play_wake_tone(snd_command: &str, ms: u32) -> anyhow::Result<()> {
    const RATE: f32 = 22_050.0;
    const FREQ: f32 = 440.0;
    // Near i16 full-scale: deep sleep needs a strong signal to wake — half-scale was measured
    // too weak on the Speak2; a full-scale ~1s 440 Hz tone wakes it (validated on-device).
    const AMP: f32 = 32_000.0;
    let samples = (RATE as usize * ms as usize) / 1000;
    let mut pcm = Vec::with_capacity(samples * 2);
    for n in 0..samples {
        let s = (AMP * (std::f32::consts::TAU * FREQ * n as f32 / RATE).sin()) as i16;
        pcm.extend_from_slice(&s.to_le_bytes());
    }
    let mut sink = PlaybackSink::start(snd_command)?;
    sink.write_pcm(&pcm).await?;
    sink.finish().await
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
        let (mut handle, mut done_rx, _task) = spawn_pump("cat >/dev/null", false);
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
        let (mut handle, mut done_rx, _task) = spawn_pump(&snd, false);
        handle.cue(vec![0u8; 8820]); // ~200 ms worth of 22050 Hz PCM
        handle.start().await.unwrap();
        handle.pcm(vec![0u8; 4410]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert!(d.result.is_ok(), "stream must not race the cue for the device: {:?}", d.result);
        let _ = std::fs::remove_file(lock);
    }

    #[tokio::test]
    async fn play_wake_tone_writes_and_drains() {
        // `cat >/dev/null` stands in for aplay: consumes the tone and exits on stdin EOF.
        play_wake_tone("cat >/dev/null", 50).await.unwrap();
    }

    #[tokio::test]
    async fn pump_playback_error_is_reported_fatally() {
        // player dies instantly (as aplay does on a busy/absent device) -> a later write EPIPEs
        let (mut handle, mut done_rx, _task) = spawn_pump("exit 1", false);
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

    // Keep-warm ON: while idle (no stream/cue), the pump holds a player open and continuously
    // feeds it digital silence — that is what keeps the Jabra's DAC warm so the next short cue
    // opens clean. `cat >> <file>` stands in for aplay so we can inspect what was fed.
    #[tokio::test]
    async fn keep_warm_feeds_silence_to_the_idle_sink_while_idle() {
        let path = std::env::temp_dir().join(format!("nabu-keepwarm-{}.raw", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let snd = format!("cat >> {}", path.display());
        let (_handle, _done_rx, task) = spawn_pump(&snd, true);
        tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        task.abort();
        tokio::time::sleep(std::time::Duration::from_millis(20)).await; // let kill_on_drop reap cat
        let bytes = std::fs::read(&path).unwrap_or_default();
        let _ = std::fs::remove_file(&path);
        assert!(!bytes.is_empty(), "keep-warm must feed the idle player while idle");
        assert!(bytes.iter().all(|&b| b == 0), "the idle keep-warm fill must be digital silence");
    }

    // Keep-warm OFF (default): nothing is played while idle, so the stand-in player is never even
    // spawned — the file is never created.
    #[tokio::test]
    async fn keep_warm_off_leaves_the_device_untouched_while_idle() {
        let path = std::env::temp_dir().join(format!("nabu-nowarm-{}.raw", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let snd = format!("cat >> {}", path.display());
        let (_handle, _done_rx, task) = spawn_pump(&snd, false);
        tokio::time::sleep(std::time::Duration::from_millis(150)).await;
        task.abort();
        let created = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        let _ = std::fs::remove_file(&path);
        assert_eq!(created, 0, "keep-warm off must not open or feed an idle player");
    }

    // Under keep-warm a cue is INJECTED into the running stream (not opened as a fresh sink), so
    // its bytes land in the same stream right after the idle silence. A non-zero cue payload
    // distinguishes the cue from the idle silence (zeros) in the shared sink file.
    #[tokio::test]
    async fn keep_warm_injects_a_cue_into_the_running_stream() {
        let path = std::env::temp_dir().join(format!("nabu-warmcue-{}.raw", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let snd = format!("cat >> {}", path.display());
        let (handle, _done_rx, task) = spawn_pump(&snd, true);
        tokio::time::sleep(std::time::Duration::from_millis(60)).await; // idle stream running silence
        handle.cue(vec![0xABu8; 8820]); // ~200 ms cue with a marker payload, must be injected
        tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        task.abort();
        tokio::time::sleep(std::time::Duration::from_millis(20)).await;
        let bytes = std::fs::read(&path).unwrap_or_default();
        let _ = std::fs::remove_file(&path);
        assert!(bytes.contains(&0xAB), "the cue must be injected into the warm running stream");
    }

    // Under keep-warm a TTS reply is ALSO injected into the running stream (no fresh per-stream
    // sink, so the reply onset is clean), and a DrainDone still fires carrying the stream's
    // generation (drives the LED; estimate-timed on this path).
    #[tokio::test]
    async fn keep_warm_injects_tts_into_the_running_stream_and_reports_drain() {
        let path = std::env::temp_dir().join(format!("nabu-warmtts-{}.raw", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let snd = format!("cat >> {}", path.display());
        let (mut handle, mut done_rx, task) = spawn_pump(&snd, true);
        tokio::time::sleep(std::time::Duration::from_millis(40)).await; // stream running silence
        handle.start().await.unwrap(); // generation 1
        handle.pcm(vec![0xCDu8; 8820]).await.unwrap(); // ~200 ms reply, must be injected
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap(); // drain fires (estimate-timed), carries the generation
        assert_eq!(d.generation, 1);
        assert!(d.result.is_ok());
        task.abort();
        tokio::time::sleep(std::time::Duration::from_millis(20)).await;
        let bytes = std::fs::read(&path).unwrap_or_default();
        let _ = std::fs::remove_file(&path);
        assert!(bytes.contains(&0xCD), "the reply must be injected into the warm running stream");
    }
}
