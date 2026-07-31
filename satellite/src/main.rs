mod audio;
mod config;
mod gpio;
mod led;
mod music;
mod satellite;
mod volume;
mod wake;
mod wyoming;

use anyhow::Context;
use config::Config;
use tokio::net::TcpListener;
use tracing::{error, info};

#[tokio::main(flavor = "multi_thread", worker_threads = 2)]
async fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::try_from_default_env()
            .unwrap_or_else(|_| "info".into()))
        .init();

    let cfg = Config::from_args()?;
    // The ring's power-on default is lit (rainbow, then direction-of-arrival), and the
    // render task only exists while a hub is connected — so blank it here to cover
    // boot -> first connect.
    led::blank_once(&cfg.led);

    // Parse + graph-optimize the wake models ONCE: per-connection loading would re-pay seconds
    // of optimization (= wake deafness) on every hub reconnect, and a bad model now fails fast
    // at boot instead of on the first connection.
    let models = cfg.wake_enabled
        .then(wake::WakeModels::load)
        .transpose()
        .context("loading wake models")?;
    let listener = TcpListener::bind(&cfg.listen).await
        .with_context(|| format!("failed to bind listen address {}", cfg.listen))?;
    info!("nabu-satellite listening on {} (hub dials in)", cfg.listen);

    let volume = volume::VolumeControl::new(cfg.volume_sink.clone(), cfg.volume_step);
    // Process-scoped, not per-connection: a hub reconnect must not forget that the user muted the
    // speaker. Seeded from the sink so wireplumber's restored state and ours agree at boot.
    volume.seed().await;

    // Single-hub policy: a new accept supersedes any previous connection. This guards the
    // dead-peer wedge — a black-holed hub TCP connection would otherwise park its writer for
    // the TCP retransmission timeout (~15 min) while holding the EXCLUSIVE mic device
    // (plughw:), starving the hub's reconnect. Aborting the stale task drops MicCapture /
    // PlaybackSink (kill_on_drop) and the button guard, so the new connection gets the devices.
    let mut active: Option<tokio::task::JoinHandle<()>> = None;
    let mut sigterm = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
    let mut sigint = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::interrupt())?;
    loop {
        let accepted = tokio::select! {
            r = listener.accept() => Some(r?),
            _ = sigterm.recv() => None,
            _ = sigint.recv() => None,
        };
        let Some((sock, peer)) = accepted else { break };
        sock.set_nodelay(true).ok();
        info!("hub connected from {peer}");
        if let Some(prev) = active.take() {
            info!("superseding previous hub connection");
            prev.abort();
            let _ = prev.await; // ensure devices are released before the new connection claims them
        }
        let (r, w) = sock.into_split();
        let cfg = cfg.clone();
        let models = models.clone();
        let volume = volume.clone();
        active = Some(tokio::spawn(async move {
            if let Err(e) = satellite::state_machine::run_connection(r, w, cfg, models, volume).await {
                error!("connection ended with error: {e:#}");
            }
        }));
    }

    // Graceful shutdown: drop the live connection so its audio devices and LED guard release,
    // then blank the ring — a stopped service must not leave it lit.
    if let Some(prev) = active.take() {
        prev.abort();
        let _ = prev.await;
    }
    led::blank_once(&cfg.led);
    info!("nabu-satellite stopped");
    Ok(())
}
