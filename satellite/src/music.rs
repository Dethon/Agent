use crate::led::LedState;
use tokio::sync::watch;
use tracing::warn;

/// Bound on how long music may stay ducked with no LED-state change. A wedged active state (a
/// no-reply turn stuck Thinking, a Listening window the hub never closes) must not hold the music
/// down until the connection drops; on expiry the loop force-restores to full and waits for the
/// next real state change before re-evaluating.
const MAX_DUCK_SECS: u64 = 30;

enum DuckerBackend {
    Real { control: String, card: Option<String> },
    #[cfg(test)]
    Probe(std::sync::Arc<std::sync::Mutex<Vec<u8>>>),
}

impl DuckerBackend {
    async fn set(&mut self, pct: u8) -> anyhow::Result<()> {
        match self {
            DuckerBackend::Real { control, card } => {
                let cmd = match card {
                    Some(c) => format!("amixer -c {c} sset {control} {pct}%"),
                    None => format!("amixer sset {control} {pct}%"),
                };
                let status = crate::audio::build_command(&cmd)
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .await?;
                anyhow::ensure!(status.success(), "amixer exited with {status}");
                Ok(())
            }
            #[cfg(test)]
            DuckerBackend::Probe(log) => {
                log.lock().unwrap().push(pct);
                Ok(())
            }
        }
    }
}

// Duck music while LISTENING to a command and while SPEAKING a reply; restore full on Thinking and
// Idle. Listening (command capture) MUST duck: with the speaker playing into the same room the mic
// would otherwise pick up the music and the hub's STT transcribes it, wrecking the command. Thinking
// and the post-reply/follow-up states RESTORE: once the command is captured the user is no longer
// talking, and the satellite can linger there (a no-reply turn sits in Thinking, a follow-up window
// in Listening->Idle) — ducking through them stranded the music down for ~10 s after the
// end-listening cue. (A MAX_DUCK safety timeout in duck_loop still backstops any wedged ducked state.)
fn target_percent(state: LedState, duck_percent: u8) -> u8 {
    match state {
        LedState::Listening | LedState::Speaking => duck_percent,
        LedState::Thinking | LedState::Idle => 100,
    }
}

async fn duck_loop(mut rx: watch::Receiver<LedState>, mut backend: DuckerBackend, duck_percent: u8) {
    let mut applied: Option<u8> = None;
    loop {
        let pct = target_percent(*rx.borrow_and_update(), duck_percent);
        if applied != Some(pct) {
            if let Err(e) = backend.set(pct).await {
                // The softvol "Music" control is created lazily the first time snapclient opens
                // the `music` PCM.  A very-early duck call (before snapclient has run) will fail
                // here and disable ducking for this connection; snapclient's Restart=always keeps
                // the control alive once it has opened, so subsequent connections self-heal.
                warn!("music duck failed, ducking disabled for this connection: {e:#}");
                return;
            }
            applied = Some(pct);
        }
        if pct >= 100 {
            // Not ducked: wait for the next state change, no deadline.
            if rx.changed().await.is_err() {
                break; // sender dropped => connection ending; DuckGuard::drop restores to full
            }
        } else {
            // Ducked: bound how long with a safety timeout so a wedged active state (no-reply turn
            // stuck Thinking, a Listening window the hub never closes) can't hold music down until
            // the connection drops.
            match tokio::time::timeout(std::time::Duration::from_secs(MAX_DUCK_SECS), rx.changed()).await {
                Ok(Ok(())) => {}     // state changed => re-evaluate
                Ok(Err(_)) => break, // sender dropped => connection ending
                Err(_) => {
                    // Timed out while ducked: force-restore, then wait for the next real state
                    // change before re-evaluating so we don't instantly re-duck the same state.
                    if let Err(e) = backend.set(100).await {
                        warn!("music duck restore failed, ducking disabled for this connection: {e:#}");
                        return;
                    }
                    applied = Some(100);
                    if rx.changed().await.is_err() {
                        break;
                    }
                }
            }
        }
    }
}

pub struct DuckGuard {
    handle: tokio::task::JoinHandle<()>,
    control: String,
    card: Option<String>,
}

impl Drop for DuckGuard {
    fn drop(&mut self) {
        self.handle.abort();
        // Fail-safe restore to full volume. abort() drops the task future at its await point and
        // skips async cleanup, so this MUST be synchronous: fire a detached std amixer (never awaited).
        let mut cmd = std::process::Command::new("amixer");
        if let Some(c) = &self.card {
            cmd.arg("-c").arg(c);
        }
        cmd.args(["sset", &self.control, "100%"])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        let _ = cmd.spawn();
    }
}

pub fn spawn_duck(
    rx: watch::Receiver<LedState>,
    music_mixer: Option<String>,
    music_card: Option<String>,
    duck_percent: u8,
) -> Option<DuckGuard> {
    let control = music_mixer?; // None => feature off (mirrors led::spawn_led returning None)
    let backend = DuckerBackend::Real { control: control.clone(), card: music_card.clone() };
    let handle = tokio::spawn(duck_loop(rx, backend, duck_percent));
    Some(DuckGuard { handle, control, card: music_card })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::led::LedState;
    use std::sync::{Arc, Mutex};
    use tokio::sync::watch;

    fn probe() -> (Arc<Mutex<Vec<u8>>>, DuckerBackend) {
        let log = Arc::new(Mutex::new(Vec::new()));
        (log.clone(), DuckerBackend::Probe(log))
    }

    #[test]
    fn ducks_on_listening_and_speaking_restores_on_thinking_and_idle() {
        // Duck only while LISTENING to a command (mic/STT must not pick up the music) and while
        // SPEAKING a reply. RESTORE on Thinking and Idle: once the command is captured the satellite
        // sits in Thinking (no-reply turn) or the follow-up Listening->Idle path, and the user is no
        // longer talking — keeping music ducked there is what stranded it down for ~10 s after the
        // end-listening cue. Thinking restores so music returns the moment the command ends.
        assert_eq!(target_percent(LedState::Idle, 20), 100);
        assert_eq!(target_percent(LedState::Listening, 20), 20);
        assert_eq!(target_percent(LedState::Thinking, 20), 100);
        assert_eq!(target_percent(LedState::Speaking, 20), 20);
        assert_eq!(target_percent(LedState::Listening, 0), 0); // honors duck_percent (0 = mute)
    }

    async fn wait_for(log: &Arc<Mutex<Vec<u8>>>, len: usize) {
        for _ in 0..1000 {
            if log.lock().unwrap().len() >= len { return; }
            tokio::task::yield_now().await;
        }
        panic!("timed out waiting for duck call #{len}; got {:?}", log.lock().unwrap());
    }

    #[tokio::test]
    async fn ducks_on_speaking_and_restores_when_it_ends() {
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;                 // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Speaking).unwrap();    // satellite speaks -> duck 20
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tx.send(LedState::Idle).unwrap();        // speech ends -> restore 100
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
        assert_eq!(log.lock().unwrap().len(), 3);
    }

    #[tokio::test]
    async fn ducks_on_listening_command_and_restores() {
        // The new behavior: music ducks the moment the satellite starts LISTENING to a command
        // (so the mic/STT isn't corrupted by the music), and restores when the turn goes Idle.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;                 // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap();   // command capture -> duck 20
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tx.send(LedState::Idle).unwrap();        // turn ended -> restore 100
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
        assert_eq!(log.lock().unwrap().len(), 3);
    }

    #[tokio::test]
    async fn restores_on_thinking_then_reducks_for_the_reply() {
        // The fix for the ~10 s strand: after the command is captured the LED goes Thinking, which
        // RESTORES the music (the user is done talking). The reply re-ducks on Speaking; the turn
        // ends restored. A no-reply turn simply stops at the Thinking restore — no strand.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100); // Idle -> 100
        tx.send(LedState::Listening).unwrap(); // command -> duck
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Thinking).unwrap(); // command captured -> RESTORE (the fix)
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);
        tx.send(LedState::Speaking).unwrap(); // reply -> duck again
        wait_for(&log, 4).await;
        assert_eq!(log.lock().unwrap()[3], 20);
        tx.send(LedState::Idle).unwrap(); // done -> restore
        wait_for(&log, 5).await;
        assert_eq!(log.lock().unwrap()[4], 100);

        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn ducked_state_force_restores_after_max_duck() {
        // Safety backstop: an active state that persists with no further change must not hold the
        // music ducked forever — after MAX_DUCK with no state change it force-restores to full.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;                 // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap();   // command -> duck
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 1)).await;
        wait_for(&log, 3).await;                 // no change for MAX_DUCK -> safety restore
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
    }
}