use crate::led::LedState;
use tokio::sync::watch;
use tracing::warn;

/// Bound on how long music may stay ducked in the LISTENING state with no LED-state change. A mic
/// window the hub never closes would otherwise hold the music down until the connection drops; on
/// expiry the loop force-restores to full and waits for the next real state change. SPEAKING is
/// exempt: a long reply legitimately holds it for tens of seconds, it is bounded by drain-completion,
/// and a genuinely-stuck Speaking is bounded by connection teardown (DuckGuard).
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

async fn duck_loop(
    mut rx: watch::Receiver<LedState>,
    mut backend: DuckerBackend,
    duck_percent: u8,
    restore_grace: std::time::Duration,
) {
    let mut applied: Option<u8> = None;
    loop {
        let state = *rx.borrow_and_update();
        let pct = target_percent(state, duck_percent);
        tracing::debug!(?state, pct, ?applied, "music duck");
        if pct < 100 {
            // Active (Listening/Speaking): duck immediately. The softvol "Music" control is created
            // lazily when snapclient first opens the `music` PCM; a very-early call can fail here and
            // disable ducking for this connection — Restart=always self-heals subsequent ones.
            if applied != Some(pct) {
                if let Err(e) = backend.set(pct).await {
                    warn!("music duck failed, ducking disabled for this connection: {e:#}");
                    return;
                }
                applied = Some(pct);
            }
            if state == LedState::Speaking {
                // Speaking is active playback (plus its drain tail), bounded by drain-completion — a
                // reply legitimately holds it for its whole duration, tens of seconds for a long
                // answer. It must NOT be force-restored: capping it flapped the music up ~0.5 s
                // before a ~30 s reply's drain finished, then re-ducked on the follow-up chime. A
                // genuinely-stuck Speaking is bounded by connection teardown (DuckGuard restores on
                // drop), so wait with no deadline.
                if rx.changed().await.is_err() { break; }
            } else {
                // Listening can wedge (a mic window the hub never closes) with no natural end, so
                // bound the ducked wait with a MAX_DUCK safety that force-restores rather than
                // holding music down until the connection drops.
                match tokio::time::timeout(std::time::Duration::from_secs(MAX_DUCK_SECS), rx.changed()).await {
                    Ok(Ok(())) => {}
                    Ok(Err(_)) => break,
                    Err(_) => {
                        if let Err(e) = backend.set(100).await {
                            warn!("music duck restore failed, ducking disabled for this connection: {e:#}");
                            return;
                        }
                        applied = Some(100);
                        if rx.changed().await.is_err() { break; }
                    }
                }
            }
        } else if applied == Some(100) {
            // Already restored: wait for the next change, no deadline.
            if rx.changed().await.is_err() { break; }
        } else if applied.is_none() {
            // First evaluation and inactive: establish the un-ducked baseline immediately.
            if let Err(e) = backend.set(100).await {
                warn!("music duck failed, ducking disabled for this connection: {e:#}");
                return;
            }
            applied = Some(100);
            if rx.changed().await.is_err() { break; }
        } else {
            // Inactive (Thinking/Idle) while ducked: DEBOUNCE the restore. A long reply arrives as
            // multiple audio segments, each ending in a brief Idle gap; restoring on every gap flaps
            // the music up between segments. Hold the duck for restore_grace; if an active state (the
            // next segment's Speaking) resumes within it, stay ducked. Only a truly-finished reply
            // (grace elapses with no new speech) restores — which also bounds a no-reply turn's strand.
            match tokio::time::timeout(restore_grace, rx.changed()).await {
                Ok(Ok(())) => {}     // state changed => re-evaluate (may re-duck or keep waiting)
                Ok(Err(_)) => break, // sender dropped => connection ending
                Err(_) => {
                    if let Err(e) = backend.set(100).await {
                        warn!("music duck restore failed, ducking disabled for this connection: {e:#}");
                        return;
                    }
                    applied = Some(100);
                    if rx.changed().await.is_err() { break; }
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
    restore_grace: std::time::Duration,
) -> Option<DuckGuard> {
    let control = music_mixer?; // None => feature off (mirrors led::spawn_led returning None)
    let backend = DuckerBackend::Real { control: control.clone(), card: music_card.clone() };
    let handle = tokio::spawn(duck_loop(rx, backend, duck_percent, restore_grace));
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

    // Long enough that non-paused tests never hit it; paused tests advance past it.
    const TEST_GRACE: std::time::Duration = std::time::Duration::from_secs(2);

    // Let the spawned duck_loop run until it parks (on a watch change or a timeout), so a following
    // assert / time-advance observes a settled state. (Single-threaded test runtime.)
    async fn settle() {
        for _ in 0..50 { tokio::task::yield_now().await; }
    }

    #[tokio::test]
    async fn ducks_immediately_on_active_and_baselines_on_idle() {
        // Duck is immediate on an active state; the first Idle establishes the un-ducked baseline;
        // an active->active change keeps the same duck level (no redundant set).
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, std::time::Duration::from_secs(600)));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100); // Idle baseline, immediate
        tx.send(LedState::Speaking).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20); // ducked immediately
        tx.send(LedState::Listening).unwrap(); // still active -> stays 20 (no new set)
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn restore_is_debounced_after_a_reply_ends() {
        // A finished reply (Speaking -> Idle) does NOT restore immediately; it holds the duck for
        // restore_grace, then restores. Same debounce bounds a no-reply turn's strand.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Speaking).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Idle).unwrap(); // reply ends -> debounced, NOT immediate
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "un-duck is debounced");
        tokio::time::advance(TEST_GRACE / 2).await;
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "still ducked mid-grace");
        tokio::time::advance(TEST_GRACE).await; // past the grace
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn stays_ducked_across_a_reply_segment_gap() {
        // THE fix: a long reply arrives in segments (Speaking -> Idle gap -> Speaking). If the next
        // segment resumes within the grace, the Idle gap must NOT flap the music back up.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Speaking).unwrap(); // segment 1
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Idle).unwrap(); // inter-segment gap
        settle().await;
        tokio::time::advance(TEST_GRACE / 2).await; // partway through the grace
        settle().await;
        tx.send(LedState::Speaking).unwrap(); // segment 2 resumes within the grace
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "no restore between segments — stayed ducked");
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn no_reply_turn_restores_after_grace() {
        // A command with no reply (Listening -> Thinking, no Speaking) restores after the grace so
        // the music doesn't strand ducked — but not instantly (that delay is what bridges gaps).
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));
        wait_for(&log, 1).await;
        assert_eq!(log.lock().unwrap()[0], 100);
        tx.send(LedState::Listening).unwrap();
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);
        tx.send(LedState::Thinking).unwrap(); // command captured, no reply
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "debounced, not instant");
        tokio::time::advance(TEST_GRACE + std::time::Duration::from_millis(1)).await;
        wait_for(&log, 3).await;
        assert_eq!(log.lock().unwrap()[2], 100);
        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn long_speaking_reply_is_not_force_restored_by_max_duck() {
        // A single long reply holds Speaking continuously for longer than MAX_DUCK (a ~30 s spoken
        // answer streams as one uninterrupted playback job). The safety cap must NOT fire on active
        // playback — it is bounded by drain-completion — because force-restoring mid-reply flaps the
        // music up under the agent's voice, then re-ducks on the follow-up chime. Only a wedged
        // Listening window (a mic the hub never closes) is capped; a stuck Speaking is bounded by
        // connection teardown (DuckGuard).
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));

        wait_for(&log, 1).await; // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Speaking).unwrap(); // reply audio starts
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 5)).await; // long reply
        settle().await;
        assert_eq!(log.lock().unwrap().len(), 2, "Speaking must not be force-restored mid-reply");

        drop(tx);
        let _ = h.await;
    }

    #[tokio::test(start_paused = true)]
    async fn ducked_state_force_restores_after_max_duck() {
        // Safety backstop: a wedged ACTIVE state must not hold music ducked forever — after MAX_DUCK
        // with no state change it force-restores to full.
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20, TEST_GRACE));

        wait_for(&log, 1).await; // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap(); // command -> duck
        wait_for(&log, 2).await;
        assert_eq!(log.lock().unwrap()[1], 20);

        tokio::time::advance(std::time::Duration::from_secs(MAX_DUCK_SECS + 1)).await;
        wait_for(&log, 3).await; // no change for MAX_DUCK -> safety restore
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
    }
}