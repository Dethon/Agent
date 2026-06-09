use crate::audio::capture::MicCapture;
use crate::audio::cues::Cues;
use crate::audio::playback::PlaybackSink;
use crate::config::Config;
use crate::gpio;
use crate::wake::WakeDetector;
use crate::wyoming::codec::{read_event_buffered, write_event};
use crate::wyoming::WyomingEvent;
use serde_json::json;
use std::collections::VecDeque;
use tokio::io::{AsyncWrite, BufReader};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::sync::mpsc;
use tracing::{info, warn};

#[derive(PartialEq, Clone, Copy, Debug)]
enum Mode { Idle, Streaming }

pub async fn run_connection(reader: OwnedReadHalf, writer: OwnedWriteHalf, cfg: Config) -> anyhow::Result<()> {
    let mut buf = BufReader::new(reader);
    let mut wr = writer;
    let mut mic = MicCapture::spawn(&cfg.mic_command)?;
    let mut detector = if cfg.wake_enabled { Some(WakeDetector::new(cfg.detector.clone())?) } else { None };
    let cues = Cues::new(&cfg)?;

    // Button is claimed per-connection, released on disconnect (ButtonGuard drop). An "empty"
    // receiver (sender already dropped) leaves the select! branch permanently disabled.
    let (_button_guard, mut button_rx) = match gpio::spawn_button(&cfg.button) {
        Ok(Some((g, rx))) => (Some(g), rx),
        Ok(None) => (None, mpsc::channel(1).1),
        Err(e) => { warn!("button unavailable: {e:#}"); (None, mpsc::channel(1).1) }
    };

    // Pre-roll ring: keep the last `preroll_chunks()` mic chunks while Idle, so a request spoken
    // immediately after the wake word/button is never clipped (the zero-lag requirement).
    let preroll_cap = cfg.preroll_chunks();
    let mut preroll: VecDeque<Vec<i16>> = VecDeque::with_capacity(preroll_cap + 1);

    let mut mode = Mode::Idle;
    let mut playback: Option<PlaybackSink> = None;

    loop {
        tokio::select! {
            ev = read_event_buffered(&mut buf) => match ev? {
                None => { info!("hub disconnected"); break; }
                Some(e) => handle_hub_event(e, &mut mode, detector.as_mut(), &mut wr, &mut playback, &cues, &cfg).await?,
            },
            chunk = mic.next_chunk() => match chunk? {
                None => { warn!("mic stream ended"); break; }
                Some(samples) => match mode {
                    Mode::Idle => {
                        push_preroll(&mut preroll, samples.clone(), preroll_cap);
                        if let Some(d) = detector.as_mut() {
                            if d.push_chunk(&samples) {
                                info!("wake word detected");
                                trim_preroll(&mut preroll, cfg.wake_preroll_chunks());
                                start_turn(&mut wr, &mut mode, &cues, &mut preroll).await?;
                            }
                        }
                    }
                    Mode::Streaming => {
                        write_event(&mut wr, &WyomingEvent::audio_chunk(16000, 2, 1, to_pcm(&samples))).await?;
                    }
                },
            },
            Some(()) = button_rx.recv() => {
                if mode == Mode::Idle {
                    info!("button pressed -> start turn");
                    if let Some(d) = detector.as_mut() { d.reset(); }
                    start_turn(&mut wr, &mut mode, &cues, &mut preroll).await?;
                }
            }
        }
    }
    Ok(())
}

fn push_preroll(buf: &mut VecDeque<Vec<i16>>, chunk: Vec<i16>, cap: usize) {
    buf.push_back(chunk);
    while buf.len() > cap { buf.pop_front(); }
}

/// Wake-path trim: keep only the newest `keep` chunks (the detection-latency gap),
/// dropping the wake-word audio that precedes them. Button turns skip this — speech
/// may legitimately precede a button press, so they flush the full ring.
fn trim_preroll(buf: &mut VecDeque<Vec<i16>>, keep: usize) {
    while buf.len() > keep { buf.pop_front(); }
}

fn to_pcm(samples: &[i16]) -> Vec<u8> {
    samples.iter().flat_map(|s| s.to_le_bytes()).collect()
}

/// On trigger: announce the pipeline, play the awake cue, then FLUSH the pre-roll to the hub
/// before going live. This is the zero-lag guarantee — buffered audio reaches the hub regardless
/// of how fast the user starts speaking or how long the hub takes to open its capture.
async fn start_turn<W: AsyncWrite + Unpin>(
    wr: &mut W, mode: &mut Mode, cues: &Cues, preroll: &mut VecDeque<Vec<i16>>,
) -> anyhow::Result<()> {
    write_event(wr, &WyomingEvent::new("run-pipeline")).await?;
    cues.play_awake();
    for chunk in preroll.drain(..) {
        write_event(wr, &WyomingEvent::audio_chunk(16000, 2, 1, to_pcm(&chunk))).await?;
    }
    *mode = Mode::Streaming;
    Ok(())
}

async fn handle_hub_event<W: AsyncWrite + Unpin>(
    e: WyomingEvent,
    mode: &mut Mode,
    detector: Option<&mut WakeDetector>,
    wr: &mut W,
    playback: &mut Option<PlaybackSink>,
    cues: &Cues,
    cfg: &Config,
) -> anyhow::Result<()> {
    match e.event_type.as_str() {
        "run-satellite" => info!("run-satellite: armed"),
        "transcript" => {
            if *mode == Mode::Streaming {
                write_event(wr, &WyomingEvent::with_data("audio-stop", json!({"timestamp":0}))).await?;
                *mode = Mode::Idle;
                if let Some(d) = detector { d.reset(); }
                cues.play_done();
            }
        }
        // Playback failures below are connection-fatal BY CHOICE: the hub redials and a fresh
        // connection re-arms everything; best-effort-continue would hide a dead audio device.
        "audio-start" => {
            if let Some(p) = playback.take() { p.kill().await; }
            *playback = Some(PlaybackSink::start(&cfg.snd_command)?);
        }
        "audio-chunk" => { if let Some(p) = playback.as_mut() { p.write_pcm(&e.payload).await?; } }
        "audio-stop" => { if let Some(p) = playback.take() { p.finish().await?; } }
        other => warn!("ignoring event {other}"),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*; // brings Mode, start_turn, handle_hub_event, and the types they use
    use crate::wyoming::codec::read_event;
    use serde_json::json;

    fn cues() -> Cues {
        Cues::new(&Config { snd_command: "cat >/dev/null".into(), ..Config::default() }).unwrap()
    }

    // THE zero-lag guarantee: a turn flushes the entire pre-roll buffer to the hub (after
    // run-pipeline) before any live audio, so speech right after the wake word isn't clipped.
    #[tokio::test]
    async fn start_turn_flushes_preroll_before_streaming() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        for _ in 0..5 { preroll.push_back(vec![0i16; 1280]); }

        start_turn(&mut a, &mut mode, &c, &mut preroll).await.unwrap();

        assert_eq!(mode, Mode::Streaming);
        assert!(preroll.is_empty(), "pre-roll must be drained on trigger");
        // One persistent BufReader: read_event re-wraps per call and would drop read-ahead
        // bytes between sequential reads (see codec.rs note) — use the buffered reader here.
        let mut buf = BufReader::new(b);
        assert_eq!(read_event_buffered(&mut buf).await.unwrap().unwrap().event_type, "run-pipeline");
        for _ in 0..5 {
            assert_eq!(read_event_buffered(&mut buf).await.unwrap().unwrap().event_type, "audio-chunk");
        }
    }

    // Wake-path regression: the flushed pre-roll must NOT include the wake word itself —
    // only the detection-latency gap (wake fires ~181 ms after the word ends). Saying
    // "ok nabu" then nothing must not transcribe-and-dispatch "ok nabu".
    #[tokio::test]
    async fn wake_trim_keeps_only_the_detection_gap() {
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        for i in 0..13 {
            preroll.push_back(vec![i as i16; 1280]); // 13 chunks ≈ the 1000 ms ring, oldest first
        }
        trim_preroll(&mut preroll, 3);
        assert_eq!(preroll.len(), 3);
        // the NEWEST chunks survive (10, 11, 12), the wake-word audio (older) is dropped
        assert_eq!(preroll[0][0], 10);
        assert_eq!(preroll[2][0], 12);
    }

    #[tokio::test]
    async fn transcript_ends_turn_with_audio_stop_and_rearms() {
        let (mut a, mut b) = tokio::io::duplex(4096);
        let c = cues();
        let mut mode = Mode::Streaming;
        let mut playback: Option<PlaybackSink> = None;
        let e = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &c, &Config::default()).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        assert_eq!(read_event(&mut b).await.unwrap().unwrap().event_type, "audio-stop");
    }

    #[tokio::test]
    async fn transcript_while_idle_is_a_noop() {
        let (mut a, b) = tokio::io::duplex(4096);
        let c = cues();
        let mut mode = Mode::Idle;
        let mut playback: Option<PlaybackSink> = None;
        let e = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &c, &Config::default()).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        // nothing must have been written to the hub
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(crate::wyoming::codec::read_event_buffered(&mut buf).await.unwrap().is_none());
    }
}