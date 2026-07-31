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

    /// Whether the player process is already gone. Non-blocking. A sink that spawned fine can
    /// still die milliseconds later — `aplay` against an undefined ALSA PCM fails its device open
    /// and exits, and with stderr nulled the exit is the only trace. An errored wait counts as
    /// gone too: we can no longer vouch for the child, and the only caller prefers a fallback.
    pub fn has_exited(&mut self) -> bool {
        !matches!(self.child.try_wait(), Ok(None))
    }

    /// Kill immediately (used if a new stream preempts an in-flight one).
    pub async fn kill(mut self) {
        let _ = self.child.kill().await;
    }
}

/// Commands accepted by the playback pump — the single owner of the playback device.
pub enum PlaybackCmd {
    /// Begin a stream (kills a still-open previous stream: mid-stream preempt). `alert` routes
    /// the stream to the alert sink — a non-attenuated ALSA route on music units, so a timer or
    /// alarm bypasses the calibrated voice level.
    Start { generation: u64, alert: bool },
    Pcm(Vec<u8>),
    /// End the stream: close stdin, let the player drain, then report a DrainDone.
    Stop { generation: u64 },
    /// Best-effort short earcon: plays only when no stream is active; errors are non-fatal.
    Cue(Vec<u8>),
    /// Same as `Cue`, plus an acknowledgement sent once the sound has finished — or immediately
    /// if it was dropped. The local mute needs it: muting the sink would otherwise cut off the
    /// cue that confirms the mute.
    CueThen(Vec<u8>, tokio::sync::oneshot::Sender<()>),
}

/// Completion report for a Stop — and the carrier for fatal playback errors.
pub struct DrainDone {
    pub generation: u64,
    pub result: anyhow::Result<()>,
}

/// Main-loop side of the pump. Stream sends await on the bounded channel, preserving the
/// flow control that writing into the player pipe used to provide; cues are fire-and-forget.
/// `generation` is atomic (not a plain counter behind `&mut self`) because the handle now lives
/// inside the connection's `Ctx` — one instance shared by reference across the whole connection,
/// never exclusively borrowed, same as `cues`/`led`/`volume`/`alert_held` there. All access is
/// still from the single connection task, so `Ordering::Relaxed` is enough: there is no other
/// thread to synchronize with, only a `&mut self` receiver to avoid.
pub struct PlaybackHandle {
    cmd_tx: mpsc::Sender<PlaybackCmd>,
    generation: std::sync::atomic::AtomicU64,
}

impl PlaybackHandle {
    pub async fn start(&self, alert: bool) -> anyhow::Result<()> {
        let generation = self.generation.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1;
        self.send(PlaybackCmd::Start { generation, alert }).await
    }

    pub async fn pcm(&self, pcm: Vec<u8>) -> anyhow::Result<()> {
        self.send(PlaybackCmd::Pcm(pcm)).await
    }

    pub async fn stop(&self) -> anyhow::Result<()> {
        let generation = self.generation.load(std::sync::atomic::Ordering::Relaxed);
        self.send(PlaybackCmd::Stop { generation }).await
    }

    /// try_send on purpose: when the pump is backlogged a late cue is worse than no cue.
    pub fn cue(&self, pcm: Vec<u8>) {
        let _ = self.cmd_tx.try_send(PlaybackCmd::Cue(pcm));
    }

    /// try_send like `cue`. A failed send drops the sender, so the waiter resolves at once and
    /// the caller proceeds — which is the wanted behaviour when the pump is backlogged.
    pub fn cue_then(&self, pcm: Vec<u8>, done: tokio::sync::oneshot::Sender<()>) {
        let _ = self.cmd_tx.try_send(PlaybackCmd::CueThen(pcm, done));
    }

    pub fn latest_generation(&self) -> u64 {
        self.generation.load(std::sync::atomic::Ordering::Relaxed)
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
    alert_snd_command: &str,
) -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, tokio::task::JoinHandle<()>) {
    let (cmd_tx, cmd_rx) = mpsc::channel(16);
    // Unbounded ON PURPOSE: a bounded blocking send from the pump could AB-deadlock against a
    // main loop blocked sending a command. Completions are tiny and at most one per stream.
    let (done_tx, done_rx) = mpsc::unbounded_channel();
    let task = tokio::spawn(run_pump(
        snd_command.to_string(),
        alert_snd_command.to_string(),
        cmd_rx,
        done_tx,
    ));
    (PlaybackHandle { cmd_tx, generation: std::sync::atomic::AtomicU64::new(0) }, done_rx, task)
}

async fn run_pump(
    snd_command: String,
    alert_snd_command: String,
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
            PlaybackCmd::Start { generation: g, alert } => {
                generation = g;
                streaming = true;
                if let Some(p) = sink.take() { p.kill().await; } // mid-stream preempt
                open_sink(&snd_command, &alert_snd_command, alert).await.map(|p| sink = Some(p))
            }
            PlaybackCmd::Pcm(pcm) => match sink.as_mut() {
                Some(p) => p.write_pcm(&pcm).await,
                None => Ok(()), // stream already gone; drop the chunk
            },
            PlaybackCmd::Stop { generation: g } => {
                streaming = false;
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
            PlaybackCmd::CueThen(pcm, done) => {
                if !streaming {
                    if let Err(e) = play_cue(&snd_command, &pcm).await {
                        tracing::warn!("cue playback failed: {e:#}");
                    }
                }
                // Acknowledge on BOTH paths — played and dropped — so a caller sequencing an
                // action after the sound is never left waiting on one that will not come.
                let _ = done.send(());
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

/// How long to let a freshly spawned alert player prove it survived its device open. Generous on
/// purpose: guessing too short costs the reconnect loop this probe exists to prevent, while
/// guessing too long costs only that much extra silence at the very start of a ring — and an
/// `aplay` whose ALSA config lookup fails is dead within a few ms of exec even on a Pi.
const ALERT_LIVENESS_PROBE: std::time::Duration = std::time::Duration::from_millis(50);

/// Open the sink for one stream. Playback-open errors are connection-fatal by design, so an
/// alert whose dedicated device is missing falls back to the normal sink instead: an alarm that
/// rings quietly beats one that drops the hub connection. Only the normal sink failing is fatal.
///
/// A missing device has two shapes and both must fall back. `spawn()` fails only when the player
/// binary is missing; the realistic one — an undefined `pcm.alert` — spawns fine and dies on the
/// device open, invisible until writes EPIPE well into the ring, so the alert sink is probed for
/// liveness before the stream is committed to it. The probe runs here, inside the pump task, which
/// is the only place compound playback I/O is allowed. Both routes are skipped when the two
/// commands are identical (the default), so a voice-only unit pays nothing and a genuine device
/// failure reports once rather than twice.
async fn open_sink(snd: &str, alert_snd: &str, alert: bool) -> anyhow::Result<PlaybackSink> {
    if !alert || alert_snd == snd {
        return PlaybackSink::start(snd);
    }
    match PlaybackSink::start(alert_snd) {
        Ok(mut p) => {
            tokio::time::sleep(ALERT_LIVENESS_PROBE).await;
            if !p.has_exited() {
                return Ok(p);
            }
            tracing::warn!("alert sink died on open, falling back to the normal sink");
            // p drops here, before the normal sink opens: never two sinks on one device.
        }
        Err(e) => tracing::warn!("alert sink unavailable, falling back to the normal sink: {e:#}"),
    }
    PlaybackSink::start(snd)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Plain "cat" sink, matching the shape most tests here need: a pump that will actually
    // play whatever is written to it.
    fn pump() -> (PlaybackHandle, mpsc::UnboundedReceiver<DrainDone>, tokio::task::JoinHandle<()>)
    {
        spawn_pump("cat >/dev/null", "cat >/dev/null")
    }

    /// The mute path depends on this: the acknowledgement must arrive only AFTER the cue has
    /// actually finished playing, or the mute silences its own confirmation.
    #[tokio::test]
    async fn cue_then_acknowledges_after_the_cue_has_played() {
        let (handle, _done_rx, _task) = pump();
        let (tx, rx) = tokio::sync::oneshot::channel();
        handle.cue_then(vec![0u8; 64], tx);
        rx.await.expect("the pump must acknowledge a played cue");
    }

    /// A cue dropped because a stream is active still has to acknowledge, otherwise a pending
    /// mute would hang forever waiting for a sound that is never going to play.
    #[tokio::test]
    async fn cue_then_acknowledges_even_when_the_cue_is_dropped() {
        let (handle, _done_rx, _task) = pump();
        handle.start(false).await.unwrap();
        let (tx, rx) = tokio::sync::oneshot::channel();
        handle.cue_then(vec![0u8; 64], tx);
        rx.await.expect("a dropped cue must still acknowledge");
    }

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
        let (handle, mut done_rx, _task) = spawn_pump("cat >/dev/null", "cat >/dev/null");
        handle.start(false).await.unwrap();
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
        let (handle, mut done_rx, _task) = spawn_pump(&snd, &snd);
        handle.cue(vec![0u8; 8820]); // ~200 ms worth of 22050 Hz PCM
        handle.start(false).await.unwrap();
        handle.pcm(vec![0u8; 4410]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert!(d.result.is_ok(), "stream must not race the cue for the device: {:?}", d.result);
        let _ = std::fs::remove_file(lock);
    }

    #[tokio::test]
    async fn pump_playback_error_is_reported_fatally() {
        // A NORMAL-sink failure stays fatal — the alert fallback above is narrowly scoped and does
        // not apply here. `exit 1` is plain argv (no sh metacharacters), so it execs a nonexistent
        // `exit` binary and spawn() fails outright at Start; a player that spawned and then died
        // reaches the same place via EPIPE on a later write. Either must surface as fatal.
        let (handle, mut done_rx, _task) = spawn_pump("exit 1", "exit 1");
        handle.start(false).await.unwrap();
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
        let (_handle, _done_rx, task) = spawn_pump(&snd, &snd);
        tokio::time::sleep(std::time::Duration::from_millis(150)).await;
        task.abort();
        let created = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        let _ = std::fs::remove_file(&path);
        assert_eq!(created, 0, "an idle pump must not open or feed a player");
    }

    // Unique temp paths per test: the suite runs in-process in parallel, so a shared name would
    // let two tests append to the same file.
    fn sink_paths(tag: &str) -> (std::path::PathBuf, std::path::PathBuf) {
        let dir = std::env::temp_dir();
        let pid = std::process::id();
        let normal = dir.join(format!("nabu-{tag}-normal-{pid}.raw"));
        let alert = dir.join(format!("nabu-{tag}-alert-{pid}.raw"));
        let _ = std::fs::remove_file(&normal);
        let _ = std::fs::remove_file(&alert);
        (normal, alert)
    }

    fn cleanup(paths: &[&std::path::Path]) {
        for p in paths {
            let _ = std::fs::remove_file(p);
        }
    }

    // THE routing guarantee: an alert stream (timer/alarm) must open the alert sink and must not
    // touch the normal one. `cat >> <file>` creates its file on open, so "the normal file does
    // not exist" proves the normal player was never even spawned.
    #[tokio::test]
    async fn pump_routes_an_alert_stream_to_the_alert_sink() {
        let (normal, alert) = sink_paths("route-alert");
        let (handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();
        assert!(d.result.is_ok());

        assert_eq!(std::fs::metadata(&alert).map(|m| m.len()).unwrap_or(0), 64);
        assert!(!normal.exists(), "an alert stream must not open the normal sink");
        cleanup(&[&normal, &alert]);
    }

    #[tokio::test]
    async fn pump_routes_a_normal_stream_to_the_normal_sink() {
        let (normal, alert) = sink_paths("route-normal");
        let (handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(false).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        done_rx.recv().await.unwrap();

        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 64);
        assert!(!alert.exists(), "a reply must not open the alert sink");
        cleanup(&[&normal, &alert]);
    }

    // Cues are voice-class earcons (awake/done/chime), never alerts — even immediately after an
    // alert stream has set the pump's most recent generation.
    #[tokio::test]
    async fn cues_always_play_on_the_normal_sink() {
        let (normal, alert) = sink_paths("route-cue");
        let (handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            &format!("cat >> {}", alert.display()),
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        done_rx.recv().await.unwrap(); // stream over -> cues are no longer dropped

        handle.cue(vec![1u8; 32]);
        for _ in 0..200 {
            if std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0) == 32 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 32);
        cleanup(&[&normal, &alert]);
    }

    // The REALISTIC alert-device failure: an `aplay -D alert` against an undefined `pcm.alert`
    // spawns FINE, then fails its ALSA open and exits within milliseconds with stderr nulled.
    // `false` is exactly that shape — a real binary (so spawn succeeds, unlike the ENOENT case
    // below) that exits 1 immediately. Without a liveness probe the stream's bytes go into a dead
    // pipe and the eventual EPIPE reports fatal, tearing the hub connection down for the whole
    // duration of the alarm — strictly worse than a quiet alarm.
    #[tokio::test]
    async fn alert_sink_dying_right_after_spawn_falls_back_to_the_normal_sink() {
        let (normal, _) = sink_paths("route-dead-alert");
        let (handle, mut done_rx, _task) =
            spawn_pump(&format!("cat >> {}", normal.display()), "false");

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();

        assert_eq!(
            std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0),
            64,
            "a dead alert sink must hand the stream to the normal sink"
        );
        assert!(d.result.is_ok(), "a dead alert sink must not be fatal: {:?}", d.result);
        cleanup(&[&normal]);
    }

    // An absent/misconfigured alert device must make the alarm QUIET, not drop the hub connection.
    // A nonexistent binary is plain argv, so build_command execs it directly and spawn() fails
    // with ENOENT — the other half of "the device can't be opened".
    #[tokio::test]
    async fn alert_sink_open_failure_falls_back_to_the_normal_sink_non_fatally() {
        let (normal, _) = sink_paths("route-fallback");
        let (handle, mut done_rx, _task) = spawn_pump(
            &format!("cat >> {}", normal.display()),
            "/nonexistent/aplay -D alert",
        );

        handle.start(true).await.unwrap();
        handle.pcm(vec![7u8; 64]).await.unwrap();
        handle.stop().await.unwrap();
        let d = done_rx.recv().await.unwrap();

        assert!(d.result.is_ok(), "an unavailable alert sink must not be fatal: {:?}", d.result);
        assert_eq!(std::fs::metadata(&normal).map(|m| m.len()).unwrap_or(0), 64);
        cleanup(&[&normal]);
    }
}
