// Wired into main.rs's accept loop and the state machine by Task 10 (this task only adds the
// module); until then nothing outside #[cfg(test)] calls in, so the whole surface reads as dead
// to a non-test build. Same "not yet wired" shape as wyoming/codec.rs's read_event.
#![allow(dead_code)]

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tracing::warn;

/// The satellite's own MASTER output level: the PipeWire sink that music, replies, cues and
/// alerts all end up in, driven with `wpctl`. This is deliberately not one of the per-source ALSA
/// softvols (`Music` / `TTS` / `Alert`) — those carry calibration, and `Music` is written by the
/// ducker on every turn. Driving the master keeps the two independent: they simply multiply.
///
/// Wireplumber persists the sink's level and mute in its own state, so nothing here is written to
/// disk and a level survives a restart on its own.
pub struct VolumeControl {
    backend: Backend,
    step: u8,
    user_muted: AtomicBool,
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
        Arc::new(Self { backend, step, user_muted: AtomicBool::new(false) })
    }

    /// pub(crate) so state_machine's tests can drive a real control without a wpctl binary.
    #[cfg(test)]
    pub(crate) fn probe_pair(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<Self>) {
        let log = Arc::new(std::sync::Mutex::new(Vec::new()));
        let control =
            Arc::new(Self { backend: Backend::Probe(log.clone()), step, user_muted: AtomicBool::new(false) });
        (log, control)
    }

    #[cfg(test)]
    fn failing(step: u8) -> Arc<Self> {
        Arc::new(Self { backend: Backend::Failing, step, user_muted: AtomicBool::new(false) })
    }

    pub fn enabled(&self) -> bool {
        !matches!(self.backend, Backend::Disabled)
    }

    pub fn user_muted(&self) -> bool {
        self.user_muted.load(Ordering::SeqCst)
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
    pub async fn step(&self, up: bool) -> anyhow::Result<()> {
        let sign = if up { '+' } else { '-' };
        self.run(&format!("set-volume -l 1.0 {{sink}} {}%{sign}", self.step)).await
    }

    /// Sets the sink only. The alert hold uses this to unmute a ringing alarm without forgetting
    /// that the user asked for silence.
    pub async fn set_sink_mute(&self, muted: bool) -> anyhow::Result<()> {
        self.run(&format!("set-mute {{sink}} {}", u8::from(muted))).await
    }

    /// Sets the sink AND records the user's intent. Rolled back on failure so a mute that never
    /// landed cannot be re-applied later by an alert release.
    pub async fn set_user_mute(&self, muted: bool) -> anyhow::Result<()> {
        if !self.enabled() {
            warn!("local mute ignored: no --volume-sink configured");
            return Ok(()); // track nothing, so a later alert release has nothing to restore
        }

        let previous = self.user_muted();
        self.user_muted.store(muted, Ordering::SeqCst);
        if let Err(e) = self.set_sink_mute(muted).await {
            self.user_muted.store(previous, Ordering::SeqCst);
            return Err(e);
        }
        Ok(())
    }

    /// Fail-safe restore for Drop, which cannot await: fire a detached std wpctl, never awaited.
    /// Same shape as music.rs's DuckGuard restore, and for the same reason.
    pub fn restore_user_mute_detached(&self) {
        let Backend::Real { sink } = &self.backend else { return };
        let mut cmd = std::process::Command::new("wpctl");
        cmd.args(["set-mute", sink, if self.user_muted() { "1" } else { "0" }])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        let _ = cmd.spawn();
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
                log.lock().unwrap().push(template.replace("{sink}", "SINK"));
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
    async fn set_sink_mute_does_not_touch_the_users_intent() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.set_sink_mute(false).await.unwrap();
        assert!(vol.user_muted(), "the hold must not clear the user's mute");
        let calls = log.lock().unwrap().clone();
        assert!(calls[0].contains("set-mute") && calls[0].ends_with('1'), "got {}", calls[0]);
        assert!(calls[1].contains("set-mute") && calls[1].ends_with('0'), "got {}", calls[1]);
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
}
