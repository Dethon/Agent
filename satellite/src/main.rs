#[allow(dead_code)]
mod audio;
#[allow(dead_code)]
mod config;
#[allow(dead_code)]
mod wake;
#[allow(dead_code)]
mod wyoming;

fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();
    println!("nabu-satellite skeleton");
    Ok(())
}