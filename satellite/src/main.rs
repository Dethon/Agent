mod audio;
mod config;
mod gpio;
mod satellite;
mod wake;
mod wyoming;

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
    let listener = TcpListener::bind(&cfg.listen).await?;
    info!("nabu-satellite listening on {} (hub dials in)", cfg.listen);

    // Single-hub policy: a new accept supersedes any previous connection. This guards the
    // dead-peer wedge — a black-holed hub TCP connection would otherwise park its writer for
    // the TCP retransmission timeout (~15 min) while holding the EXCLUSIVE mic device
    // (plughw:), starving the hub's reconnect. Aborting the stale task drops MicCapture /
    // PlaybackSink (kill_on_drop) and the button guard, so the new connection gets the devices.
    let mut active: Option<tokio::task::JoinHandle<()>> = None;
    loop {
        let (sock, peer) = listener.accept().await?;
        sock.set_nodelay(true).ok();
        if let Some(prev) = active.take() {
            info!("new hub connection supersedes the previous one");
            prev.abort();
            let _ = prev.await; // ensure devices are released before the new connection claims them
        }
        info!("hub connected from {peer}");
        let (r, w) = sock.into_split();
        let cfg = cfg.clone();
        active = Some(tokio::spawn(async move {
            if let Err(e) = satellite::state_machine::run_connection(r, w, cfg).await {
                error!("connection ended with error: {e:#}");
            }
        }));
    }
}
