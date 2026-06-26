use crate::led::LedState;
use tokio::sync::watch;
use tracing::warn;

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

fn target_percent(state: LedState, duck_percent: u8) -> u8 {
    if state == LedState::Idle { 100 } else { duck_percent }
}

async fn duck_loop(mut rx: watch::Receiver<LedState>, mut backend: DuckerBackend, duck_percent: u8) {
    let mut applied: Option<u8> = None;
    loop {
        let pct = target_percent(*rx.borrow_and_update(), duck_percent);
        if applied != Some(pct) {
            if let Err(e) = backend.set(pct).await {
                warn!("music duck failed, ducking disabled for this connection: {e:#}");
                return;
            }
            applied = Some(pct);
        }
        if rx.changed().await.is_err() {
            break; // sender dropped => connection ending; DuckGuard::drop restores to full
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
    fn idle_is_full_active_is_ducked() {
        assert_eq!(target_percent(LedState::Idle, 20), 100);
        assert_eq!(target_percent(LedState::Listening, 20), 20);
        assert_eq!(target_percent(LedState::Thinking, 20), 20);
        assert_eq!(target_percent(LedState::Speaking, 20), 20);
        assert_eq!(target_percent(LedState::Listening, 35), 35);
    }

    async fn wait_for(log: &Arc<Mutex<Vec<u8>>>, len: usize) {
        for _ in 0..1000 {
            if log.lock().unwrap().len() >= len { return; }
            tokio::task::yield_now().await;
        }
        panic!("timed out waiting for duck call #{len}; got {:?}", log.lock().unwrap());
    }

    #[tokio::test]
    async fn ducks_on_active_dedups_and_restores_on_idle() {
        let (tx, rx) = watch::channel(LedState::Idle);
        let (log, backend) = probe();
        let h = tokio::spawn(duck_loop(rx, backend, 20));

        wait_for(&log, 1).await;                 // initial Idle -> 100
        assert_eq!(log.lock().unwrap()[0], 100);

        tx.send(LedState::Listening).unwrap();
        wait_for(&log, 2).await;                 // -> 20
        assert_eq!(log.lock().unwrap()[1], 20);

        tx.send(LedState::Speaking).unwrap();    // active -> active, same 20: deduped (no new call)
        tx.send(LedState::Idle).unwrap();
        wait_for(&log, 3).await;                 // -> 100
        assert_eq!(log.lock().unwrap()[2], 100);

        drop(tx);
        let _ = h.await;
        assert_eq!(log.lock().unwrap().len(), 3, "active->active must not re-issue an amixer call");
    }
}