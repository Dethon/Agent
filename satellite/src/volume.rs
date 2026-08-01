use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::sync::Mutex;
use tracing::warn;

/// The satellite's own MASTER output level: the PipeWire sink that music, replies, cues and
/// alerts all end up in, driven with `wpctl`. This is deliberately not one of the per-source ALSA
/// softvols (`Music` / `TTS` / `Alert`) — those carry calibration, and `Music` is written by the
/// ducker on every turn. Driving the master keeps the two independent: they simply multiply.
///
/// Wireplumber persists the sink's level and mute in its own state, so nothing here is written to
/// disk and a level survives a restart on its own.
///
/// `gate` serializes `step`/`alert_hold`/`alert_release`/`set_user_mute` end-to-end (decision
/// through the awaited `wpctl` call). Without it, `set_user_mute`'s read-modify-store of
/// `user_muted` and its awaited sink write are two separate windows a concurrent call can land
/// inside — e.g. an alert hold racing a queued "mute" confirmation, which can leave `user_muted()`
/// disagreeing with whichever call's `wpctl` process actually landed last on the real sink.
/// `tokio::sync::Mutex`, not `std::sync::Mutex`, because the held region awaits.
///
/// `alert_held` lives here rather than in the connection's `Ctx` because every decision reads it
/// together with `user_muted`, which is process-scoped: keeping them apart meant `set_user_mute`
/// could not see a hold, so a mute arriving ~100 ms after an alarm started silenced the alarm.
/// Under the same gate, the two are always read and written as one consistent pair.
pub struct VolumeControl {
    backend: Backend,
    step: u8,
    user_muted: AtomicBool,
    alert_held: AtomicBool,
    gate: Mutex<()>,
}

enum Backend {
    /// No sink configured: PipeWire is installed only on music units, so a voice-only satellite
    /// has nothing to drive. Mirrors `music_mixer: None` disabling ducking.
    Disabled,
    Real { sink: String },
    #[cfg(test)]
    Probe(Arc<std::sync::Mutex<Vec<String>>>),
    #[cfg(test)]
    Failing,
}

impl VolumeControl {
    pub fn new(sink: Option<String>, step: u8) -> Arc<Self> {
        let backend = match sink {
            Some(s) => Backend::Real { sink: s },
            None => Backend::Disabled,
        };
        Arc::new(Self {
            backend,
            step,
            user_muted: AtomicBool::new(false),
            alert_held: AtomicBool::new(false),
            gate: Mutex::new(()),
        })
    }

    /// pub(crate) so state_machine's tests can drive a real control without a wpctl binary.
    #[cfg(test)]
    pub(crate) fn probe_pair(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<Self>) {
        let log = Arc::new(std::sync::Mutex::new(Vec::new()));
        let control = Arc::new(Self {
            backend: Backend::Probe(log.clone()),
            step,
            user_muted: AtomicBool::new(false),
            alert_held: AtomicBool::new(false),
            gate: Mutex::new(()),
        });
        (log, control)
    }

    #[cfg(test)]
    fn failing(step: u8) -> Arc<Self> {
        Arc::new(Self {
            backend: Backend::Failing,
            step,
            user_muted: AtomicBool::new(false),
            alert_held: AtomicBool::new(false),
            gate: Mutex::new(()),
        })
    }

    pub fn enabled(&self) -> bool {
        !matches!(self.backend, Backend::Disabled)
    }

    pub fn user_muted(&self) -> bool {
        self.user_muted.load(Ordering::SeqCst)
    }

    pub fn alert_held(&self) -> bool {
        self.alert_held.load(Ordering::SeqCst)
    }

    /// Read the sink's current mute once at startup so the satellite's idea of the user's intent
    /// matches what wireplumber restored. A failed or unparsable read leaves it unmuted, which is
    /// the safe direction: the speaker is audible and one spoken command fixes it.
    pub async fn seed(&self) {
        let Backend::Real { sink } = &self.backend else { return };
        match run_capture(&format!("wpctl get-volume {sink}")).await {
            Ok(out) => {
                let muted = out.contains("[MUTED]");
                self.user_muted.store(muted, Ordering::SeqCst);
                tracing::info!(muted, "seeded local volume mute state");
            }
            Err(e) => warn!("could not read sink mute state, assuming unmuted: {e:#}"),
        }
    }

    /// `-l 1.0` caps the sink at unity, so repeated steps up cannot push it into software gain.
    /// Gated so a step can't interleave with a mute's own read-modify-store-await sequence.
    pub async fn step(&self, up: bool) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        let sign = if up { '+' } else { '-' };
        self.run(&format!("set-volume -l 1.0 {{sink}} {}%{sign}", self.step)).await
    }

    /// The actual sink write. Callers take the gate themselves and then call this, so the gate is
    /// taken exactly once per public call — re-locking an already-held `tokio::sync::Mutex` would
    /// deadlock, since it is not reentrant.
    async fn set_sink_mute_locked(&self, muted: bool) -> anyhow::Result<()> {
        self.run(&format!("set-mute {{sink}} {}", u8::from(muted))).await
    }

    /// An alarm must ring even on a muted speaker: mark the hold and make sure the sink is
    /// audible, without clearing the user's intent — the release has to have something to restore.
    ///
    /// Idempotent, because the hub re-sends the hold on every ring round (that is what heals a
    /// satellite which reconnected mid-alarm, or was rebooting when the alarm started). A repeat
    /// while the hold already stands writes nothing. A hold whose sink write FAILS un-marks
    /// itself, so the next round's re-assert retries instead of skipping.
    pub async fn alert_hold(&self) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        if self.alert_held.swap(true, Ordering::SeqCst) {
            return Ok(());
        }
        if self.user_muted() {
            if let Err(e) = self.set_sink_mute_locked(false).await {
                self.alert_held.store(false, Ordering::SeqCst);
                return Err(e);
            }
        }
        Ok(())
    }

    /// Ends the hold and puts the sink back to whatever the user asked for — including a mute that
    /// arrived DURING the alarm and was deliberately not applied then. A release with no hold
    /// outstanding writes nothing, so a stray or duplicated one cannot mute a speaker on its own.
    pub async fn alert_release(&self) -> anyhow::Result<()> {
        let _guard = self.gate.lock().await;
        if !self.alert_held.swap(false, Ordering::SeqCst) {
            return Ok(());
        }
        self.set_sink_mute_locked(self.user_muted()).await
    }

    /// Records the user's intent and, unless an alert hold is outstanding, applies it to the sink.
    /// A mute during a ringing alarm is DEFERRED — recorded now, written by `alert_release` — so a
    /// "silencia el altavoz" said a second before a timer fires cannot silence that timer. An
    /// unmute always lands at once: it is harmless mid-alarm and is exactly what was asked for.
    ///
    /// A failed write rolls the intent back, so a mute that never landed cannot be re-applied
    /// later by an alert release. Holds the gate for the whole read-modify-await-store sequence,
    /// so a concurrent call cannot observe or land in the middle of it — see the struct's docs.
    pub async fn set_user_mute(&self, muted: bool) -> anyhow::Result<()> {
        if !self.enabled() {
            warn!("local mute ignored: no --volume-sink configured");
            return Ok(()); // track nothing, so a later alert release has nothing to restore
        }

        let _guard = self.gate.lock().await;
        let previous = self.user_muted();
        self.user_muted.store(muted, Ordering::SeqCst);
        if muted && self.alert_held() {
            return Ok(());
        }
        if let Err(e) = self.set_sink_mute_locked(muted).await {
            self.user_muted.store(previous, Ordering::SeqCst);
            return Err(e);
        }
        Ok(())
    }

    /// Fail-safe teardown for Drop, which cannot await: end the hold and fire a detached std
    /// wpctl putting the sink back to the user's intent, never awaited. Same shape as music.rs's
    /// DuckGuard restore, and for the same reason. It runs both ways — a hub that dies mid-alarm
    /// must not leave the speaker silently muted, and must not swallow a mute deferred by the
    /// hold either. Deliberately synchronous and lock-free: `gate` is an async mutex and
    /// `Drop::drop` has no executor to await it against, so this cannot take part in the
    /// serialization the other methods get. It is best-effort, firing after the connection is
    /// going away regardless, not a command that needs to serialize against anything running.
    pub fn release_hold_detached(&self) {
        if !self.alert_held.swap(false, Ordering::SeqCst) {
            return;
        }
        let muted = self.user_muted();
        match &self.backend {
            Backend::Real { sink } => {
                let mut cmd = std::process::Command::new("wpctl");
                cmd.args(["set-mute", sink, if muted { "1" } else { "0" }])
                    .stdin(std::process::Stdio::null())
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null());
                let _ = cmd.spawn();
            }
            #[cfg(test)]
            Backend::Probe(log) => {
                log.lock().unwrap().push(format!("set-mute SINK {}", u8::from(muted)));
            }
            _ => {}
        }
    }

    async fn run(&self, template: &str) -> anyhow::Result<()> {
        match &self.backend {
            Backend::Disabled => {
                warn!("local volume command ignored: no --volume-sink configured");
                Ok(())
            }
            Backend::Real { sink } => {
                let cmdline = format!("wpctl {}", template.replace("{sink}", sink));
                let status = crate::audio::build_command(&cmdline)
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .await?;
                anyhow::ensure!(status.success(), "wpctl exited with {status}");
                Ok(())
            }
            #[cfg(test)]
            Backend::Probe(log) => {
                let cmdline = template.replace("{sink}", "SINK");
                // A real wpctl call is a subprocess with variable OS-scheduling latency, so two
                // overlapping calls can complete in either order regardless of which one started
                // (and stored its bookkeeping) first. Give a "mute on" command a few extra yields
                // so a concurrency test can force that inversion deterministically on the
                // single-threaded test runtime, without any real sleeping — see
                // `concurrent_set_user_mute_calls_serialize_and_stay_consistent`.
                let extra_yields = if cmdline.ends_with('1') { 3 } else { 1 };
                for _ in 0..extra_yields {
                    tokio::task::yield_now().await;
                }
                log.lock().unwrap().push(cmdline);
                Ok(())
            }
            #[cfg(test)]
            Backend::Failing => anyhow::bail!("wpctl failed"),
        }
    }
}

async fn run_capture(cmdline: &str) -> anyhow::Result<String> {
    let out = crate::audio::build_command(cmdline)
        .stderr(std::process::Stdio::null())
        .output()
        .await?;
    anyhow::ensure!(out.status.success(), "command exited with {}", out.status);
    Ok(String::from_utf8_lossy(&out.stdout).into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn probe(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<VolumeControl>) {
        VolumeControl::probe_pair(step)
    }

    #[tokio::test]
    async fn step_up_and_down_issue_relative_wpctl_calls_capped_at_unity() {
        let (log, vol) = probe(10);
        vol.step(true).await.unwrap();
        vol.step(false).await.unwrap();
        let calls = log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2);
        assert!(calls[0].contains("set-volume"), "got {}", calls[0]);
        assert!(calls[0].contains("10%+"), "got {}", calls[0]);
        assert!(calls[0].contains("-l 1.0"), "a step must not push the sink past unity: {}", calls[0]);
        assert!(calls[1].contains("10%-"), "got {}", calls[1]);
    }

    #[tokio::test]
    async fn set_user_mute_tracks_the_users_intent() {
        let (_, vol) = probe(10);
        assert!(!vol.user_muted());
        vol.set_user_mute(true).await.unwrap();
        assert!(vol.user_muted());
        vol.set_user_mute(false).await.unwrap();
        assert!(!vol.user_muted());
    }

    /// The alert hold unmutes the sink for a ringing alarm WITHOUT forgetting that the user asked
    /// for silence — otherwise the release would have nothing to restore.
    #[tokio::test]
    async fn alert_hold_does_not_touch_the_users_intent() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.alert_hold().await.unwrap();
        assert!(vol.user_muted(), "the hold must not clear the user's mute");
        let calls = log.lock().unwrap().clone();
        assert!(calls[0].contains("set-mute") && calls[0].ends_with('1'), "got {}", calls[0]);
        assert!(calls[1].contains("set-mute") && calls[1].ends_with('0'), "got {}", calls[1]);
    }

    /// The hub re-sends the hold at the top of every ring round, so a repeat must be free: the
    /// sink is already audible and a second unmute write would spam wpctl for the whole alarm.
    #[tokio::test]
    async fn repeated_alert_hold_writes_the_sink_only_once() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        log.lock().unwrap().clear();

        vol.alert_hold().await.unwrap();
        vol.alert_hold().await.unwrap();
        vol.alert_hold().await.unwrap();

        assert_eq!(log.lock().unwrap().clone(), vec!["set-mute SINK 0".to_string()]);
    }

    /// An unmute is what the user just asked for and cannot silence anything, so unlike a mute it
    /// is never deferred by an outstanding hold.
    #[tokio::test]
    async fn unmute_during_an_alert_hold_lands_immediately() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.alert_hold().await.unwrap();
        log.lock().unwrap().clear();

        vol.set_user_mute(false).await.unwrap();

        assert!(!vol.user_muted());
        assert_eq!(log.lock().unwrap().clone(), vec!["set-mute SINK 0".to_string()]);
    }

    /// A failed hold un-marks itself so the hub's next per-round re-assert retries it. Marked but
    /// un-unmuted would leave the alarm ringing into a muted sink for its whole duration.
    #[tokio::test]
    async fn failed_alert_hold_leaves_no_hold_outstanding() {
        let vol = VolumeControl::failing(10);
        vol.user_muted.store(true, Ordering::SeqCst);

        assert!(vol.alert_hold().await.is_err());
        assert!(!vol.alert_held(), "a hold whose unmute failed must be retryable");
    }

    /// The teardown path C2's reconnect case runs through: the hub dies while a mute is deferred,
    /// so the guard has to apply that mute rather than only ever un-muting.
    #[tokio::test]
    async fn release_hold_detached_applies_a_deferred_mute() {
        let (log, vol) = probe(10);
        vol.alert_hold().await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        log.lock().unwrap().clear();

        vol.release_hold_detached();

        assert!(!vol.alert_held());
        assert_eq!(log.lock().unwrap().clone(), vec!["set-mute SINK 1".to_string()]);
    }

    /// With no hold outstanding there is nothing to restore, and writing anyway would let a
    /// teardown overwrite a sink state the satellite never changed.
    #[tokio::test]
    async fn release_hold_detached_without_a_hold_writes_nothing() {
        let (log, vol) = probe(10);
        vol.release_hold_detached();
        assert!(log.lock().unwrap().is_empty());
    }

    /// A failed set-mute must not leave the satellite believing a mute that never landed: the
    /// next alert-release would then mute a speaker the user never silenced.
    #[tokio::test]
    async fn failed_set_user_mute_rolls_the_tracked_state_back() {
        let vol = VolumeControl::failing(10);
        assert!(vol.set_user_mute(true).await.is_err());
        assert!(!vol.user_muted(), "a failed mute must not be remembered as muted");
    }

    #[tokio::test]
    async fn disabled_control_makes_every_action_a_no_op() {
        let vol = VolumeControl::new(None, 10);
        assert!(!vol.enabled());
        vol.step(true).await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        assert!(!vol.user_muted(), "a disabled control tracks nothing");
    }

    /// Reproduces the race a missing gate allows: `set_user_mute` stores its bookkeeping BEFORE
    /// awaiting its own `wpctl` call, so a second overlapping `set_user_mute` can overwrite that
    /// bookkeeping while the first call's subprocess is still in flight. The probe backend makes
    /// a "mute on" (`...1`) command land after a few more scheduler turns than a "mute off"
    /// (`...0`) one (see the `Probe` arm of `run`), which — on the single-threaded test
    /// runtime — deterministically inverts completion order relative to spawn/store order: the
    /// call that stores ITS bookkeeping first is the one whose `wpctl` call actually lands last.
    /// Without the gate that leaves `user_muted()` disagreeing with the last recorded call; with
    /// it, the two calls run fully one after another, so whichever finishes last decides both.
    #[tokio::test]
    async fn concurrent_set_user_mute_calls_serialize_and_stay_consistent() {
        let (log, vol) = probe(10);

        let mute_on = { let vol = vol.clone(); tokio::spawn(async move { vol.set_user_mute(true).await }) };
        let mute_off = { let vol = vol.clone(); tokio::spawn(async move { vol.set_user_mute(false).await }) };

        mute_on.await.unwrap().unwrap();
        mute_off.await.unwrap().unwrap();

        let calls = log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2, "got {calls:?}");
        let last_landed_muted = calls.last().unwrap().ends_with('1');
        assert_eq!(
            vol.user_muted(),
            last_landed_muted,
            "user_muted() must agree with whichever call's wpctl call actually landed last; \
             calls = {calls:?}, user_muted = {}",
            vol.user_muted()
        );
    }
}
