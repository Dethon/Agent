use crate::audio::capture::MicCapture;
use crate::audio::cues::Cues;
use crate::audio::playback::{spawn_pump, DrainDone, PlaybackHandle};
use crate::config::Config;
use crate::gpio;
use crate::led::{self, LedState};
use crate::wake::{WakeDetector, WakeModels};
use crate::wyoming::codec::{read_event_buffered, write_event};
use crate::wyoming::WyomingEvent;
use serde_json::json;
use std::collections::VecDeque;
use tokio::io::{AsyncWrite, BufReader};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::sync::{mpsc, watch};
use tracing::{info, warn};

#[derive(PartialEq, Clone, Copy, Debug)]
enum Mode { Idle, Streaming }

/// Aborts the wrapped task on drop, so the pump tasks can never outlive run_connection —
/// neither on loop exit / `?` error paths, nor when the whole connection task is aborted
/// (main.rs single-hub supersede policy; the mic pump owns MicCapture, whose kill_on_drop
/// must reap arecord before the next connection can claim the exclusive device).
struct AbortOnDrop(tokio::task::JoinHandle<()>);
impl Drop for AbortOnDrop {
    fn drop(&mut self) { self.0.abort(); }
}

/// Immutable per-connection context threaded through the event handlers (bundled to keep
/// the signatures within clippy's argument limit).
struct Ctx<'a> {
    cues: &'a Cues,
    led: &'a watch::Sender<LedState>,
}

pub async fn run_connection(
    reader: OwnedReadHalf, writer: OwnedWriteHalf, cfg: Config, models: Option<WakeModels>,
) -> anyhow::Result<()> {
    let mut wr = writer;
    let mic = MicCapture::spawn(&cfg.mic_command)?;
    let mut detector =
        models.as_ref().map(|m| WakeDetector::new(m, cfg.detector.clone())).transpose()?;
    let cues = Cues::new(&cfg)?;

    // CANCELLATION SAFETY: tokio::select! DROPS the futures of losing arms. Both
    // read_event_buffered and MicCapture::next_chunk are multi-await compound reads, so
    // dropping them mid-read loses partial progress: a hub event spanning TCP segments is
    // half-consumed and the next read parses PCM payload as a header line ("stream did not
    // contain valid UTF-8"); a half-read mic chunk drops bytes and misaligns the i16 stream.
    // The compound reads therefore live in dedicated pump tasks, and the select! below races
    // only mpsc::Receiver::recv() futures, which ARE cancellation-safe. Bounded channels
    // preserve flow control: when the main loop blocks, the pumps block on send() and the
    // socket / arecord pipe back up exactly as before. Playback writes/drains are compound
    // I/O too — they live in the playback pump (spawned below), not in this loop.
    let (hub_tx, mut hub_rx) = mpsc::channel::<anyhow::Result<WyomingEvent>>(16);
    let _hub_pump = AbortOnDrop(tokio::spawn(async move {
        // 32 KiB (vs the 8 KiB default): TTS receive bursts at 100+ frames/s; a bigger read
        // buffer quarters the read syscalls while the loop competes with playback for CPU.
        let mut buf = BufReader::with_capacity(32 * 1024, reader);
        loop {
            match read_event_buffered(&mut buf).await {
                Ok(Some(e)) => {
                    if hub_tx.send(Ok(e)).await.is_err() { break; } // main loop gone
                }
                Ok(None) => break, // clean EOF -> drop tx -> recv() yields None
                Err(e) => { let _ = hub_tx.send(Err(e)).await; break; }
            }
        }
    }));
    let (mic_tx, mut mic_rx) = mpsc::channel::<anyhow::Result<Vec<i16>>>(8);
    let _mic_pump = AbortOnDrop(tokio::spawn(async move {
        let mut mic = mic;
        loop {
            match mic.next_chunk().await {
                Ok(Some(samples)) => {
                    if mic_tx.send(Ok(samples)).await.is_err() { break; } // main loop gone
                }
                Ok(None) => break, // EOF -> drop tx -> recv() yields None
                Err(e) => { let _ = mic_tx.send(Err(e)).await; break; }
            }
        }
        // MicCapture drops here -> kill_on_drop reaps the arecord child
    }));

    // Button is claimed per-connection, released on disconnect (ButtonGuard drop). An "empty"
    // receiver (sender already dropped) leaves the select! branch permanently disabled.
    let (_button_guard, mut button_rx) = match gpio::spawn_button(&cfg.button) {
        Ok(Some((g, rx))) => (Some(g), rx),
        Ok(None) => (None, mpsc::channel(1).1),
        Err(e) => { warn!("button unavailable: {e:#}"); (None, mpsc::channel(1).1) }
    };

    // LED is claimed per-connection like the button; guard drop (connection end/supersede)
    // aborts the render task, whose backend turns the light off on drop.
    let (led_tx, led_rx) = watch::channel(LedState::Idle);
    let _led_guard = led::spawn_led(&cfg.led, led_rx);
    let ctx = Ctx { cues: &cues, led: &led_tx };

    // Playback is a pump task too: PlaybackSink::finish() waits for the player to drain
    // (≈0.5-2 s of buffered TTS after every reply) and must not park this loop — wake/button
    // re-arm and mic forwarding stay live during the drain. Completions come back on an
    // unbounded channel (a bounded send from the pump could AB-deadlock against a main loop
    // blocked sending a command) and are raced below like the other pumps.
    let (mut playback, mut playback_done, pump_task) = spawn_pump(&cfg.snd_command);
    let _playback_pump = AbortOnDrop(pump_task);

    // Pre-roll ring: keep the last `preroll_chunks()` mic chunks while Idle, so a request spoken
    // immediately after the wake word/button is never clipped (the zero-lag requirement).
    let preroll_cap = cfg.preroll_chunks();
    let mut preroll: VecDeque<Vec<i16>> = VecDeque::with_capacity(preroll_cap + 1);

    let mut mode = Mode::Idle;

    loop {
        tokio::select! {
            ev = hub_rx.recv() => match ev {
                None => { info!("hub disconnected"); break; }
                Some(Err(e)) => return Err(e),
                Some(Ok(e)) => handle_hub_event(e, &mut mode, detector.as_mut(), &mut wr, &mut playback, &ctx).await?,
            },
            done = playback_done.recv() => match done {
                None => anyhow::bail!("playback pump terminated"),
                Some(d) => apply_drain_done(d, playback.latest_generation(), mode, ctx.led)?,
            },
            chunk = mic_rx.recv() => match chunk {
                None => { warn!("mic stream ended"); break; }
                Some(Err(e)) => return Err(e),
                Some(Ok(samples)) => match mode {
                    Mode::Idle => {
                        push_preroll(&mut preroll, samples.clone(), preroll_cap);
                        if let Some(d) = detector.as_mut() {
                            let t0 = std::time::Instant::now();
                            let fired = d.push_chunk(&samples);
                            // on-device budget check: must stay well under the 80 ms chunk cadence
                            tracing::debug!(us = t0.elapsed().as_micros() as u64, "wake inference");
                            if fired {
                                info!("wake word detected");
                                trim_preroll(&mut preroll, cfg.wake_preroll_chunks());
                                start_turn(&mut wr, &mut mode, &ctx, &mut preroll, &playback).await?;
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
                    start_turn(&mut wr, &mut mode, &ctx, &mut preroll, &playback).await?;
                }
            }
        }
    }
    // _hub_pump/_mic_pump/_playback_pump drop here (as on every early-return path) and abort
    // their tasks.
    Ok(())
}

/// A reply/announcement finished draining out of the player. The Idle/Listening transition is
/// generation-gated: a stale completion arriving after a newer audio-start must not blank the
/// LED mid-Speaking. Playback failures stay connection-fatal (the hub redials and a fresh
/// connection re-arms everything; best-effort-continue would hide a dead audio device).
fn apply_drain_done(
    d: DrainDone, latest_generation: u64, mode: Mode, led: &watch::Sender<LedState>,
) -> anyhow::Result<()> {
    d.result?;
    if d.generation == latest_generation {
        let _ = led.send(if mode == Mode::Streaming { LedState::Listening } else { LedState::Idle });
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
    wr: &mut W, mode: &mut Mode, ctx: &Ctx<'_>, preroll: &mut VecDeque<Vec<i16>>,
    playback: &PlaybackHandle,
) -> anyhow::Result<()> {
    write_event(wr, &WyomingEvent::new("run-pipeline")).await?;
    if let Some(pcm) = ctx.cues.awake() { playback.cue(pcm); }
    let _ = ctx.led.send(LedState::Listening);
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
    playback: &mut PlaybackHandle,
    ctx: &Ctx<'_>,
) -> anyhow::Result<()> {
    match e.event_type.as_str() {
        "run-satellite" => info!("run-satellite: armed"),
        "transcript" => {
            if *mode == Mode::Streaming {
                write_event(wr, &WyomingEvent::with_data("audio-stop", json!({"timestamp":0}))).await?;
                *mode = Mode::Idle;
                if let Some(d) = detector { d.reset(); }
                if let Some(pcm) = ctx.cues.done() { playback.cue(pcm); }
                let _ = ctx.led.send(LedState::Thinking);
            }
        }
        // Playback errors surface as fatal through the pump's DrainDone/closed-channel paths
        // (see apply_drain_done). The pump owns the player; commands here never block on the
        // device, only on the bounded command channel (flow control).
        "audio-start" => {
            playback.start().await?;
            let _ = ctx.led.send(LedState::Speaking); // replies AND standalone announcements
        }
        "audio-chunk" => playback.pcm(e.payload).await?,
        "audio-stop" => {
            // The drain happens in the pump; the Idle/Listening LED transition fires when the
            // pump reports DrainDone (apply_drain_done), i.e. at actual playback end.
            playback.stop().await?;
        }
        other => warn!("ignoring event {other}"),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*; // brings Mode, start_turn, handle_hub_event, and the types they use
    use crate::led::LedState;
    use crate::wyoming::codec::read_event;
    use serde_json::json;
    use tokio::sync::watch;

    fn cues() -> Cues {
        Cues::new(&Config::default()).unwrap()
    }

    fn pump() -> (PlaybackHandle, tokio::sync::mpsc::UnboundedReceiver<DrainDone>, AbortOnDrop) {
        let (handle, done_rx, task) = spawn_pump("cat >/dev/null");
        (handle, done_rx, AbortOnDrop(task))
    }

    // THE zero-lag guarantee: a turn flushes the entire pre-roll buffer to the hub (after
    // run-pipeline) before any live audio, so speech right after the wake word isn't clipped.
    #[tokio::test]
    async fn start_turn_flushes_preroll_before_streaming() {
        let (mut a, b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        for _ in 0..5 { preroll.push_back(vec![0i16; 1280]); }
        let (playback, _done_rx, _pump) = pump();

        start_turn(&mut a, &mut mode, &ctx, &mut preroll, &playback).await.unwrap();

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

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Streaming;
        let (mut playback, _done_rx, _pump) = pump();
        let e = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        assert_eq!(read_event(&mut b).await.unwrap().unwrap().event_type, "audio-stop");
    }

    // Regression for a select! cancellation-safety bug: hub events arriving in fragments
    // while the mic floods chunks caused the in-flight read_event_buffered future to be
    // dropped mid-event, desyncing the stream ("stream did not contain valid UTF-8").
    // The hub side below writes each audio-chunk frame in two halves with a yield between
    // them, so pre-fix the partial-progress drop happens almost surely within ~300 events.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn survives_fragmented_hub_frames_under_mic_flood() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();

        // satellite side: mic floods (always-ready /dev/zero), playback to /dev/null
        let cfg = Config {
            mic_command: "head -c 99999999 /dev/zero".into(),
            snd_command: "cat >/dev/null".into(),
            wake_enabled: false, // detector off: keeps the loop hot on raw I/O
            button: crate::config::ButtonConfig::None,
            led: crate::config::LedConfig::None, // no /dev/spidev in CI; keep the log clean
            ..Config::default()
        };
        let sat = tokio::spawn(async move {
            let (sock, _) = listener.accept().await.unwrap();
            let (r, w) = sock.into_split();
            run_connection(r, w, cfg, None).await
        });

        // hub side: dial in, then stream fragmented audio-chunk frames
        let mut hub = tokio::net::TcpStream::connect(addr).await.unwrap();
        use tokio::io::AsyncWriteExt;
        // 0xAA is never a valid UTF-8 leading byte: a desynced reader that parses payload
        // bytes as a header line reproduces the exact live error ("stream did not contain
        // valid UTF-8") instead of a JSON parse error.
        let payload = vec![0xAAu8; 1280];
        let data = json!({"rate":22050,"width":2,"channels":1});
        let body = serde_json::to_vec(&data).unwrap();
        let header = format!(
            "{{\"type\":\"audio-chunk\",\"data_length\":{},\"payload_length\":{}}}\n",
            body.len(),
            payload.len()
        );
        // Ignore hub-side write errors: pre-fix the satellite tears the connection down
        // mid-stream, and the interesting failure is the satellite's own error below.
        let hub_io = async {
            hub.write_all(b"{\"type\":\"run-satellite\"}\n").await?;
            hub.write_all(b"{\"type\":\"audio-start\",\"data\":{\"rate\":22050,\"width\":2,\"channels\":1}}\n").await?;
            for _ in 0..300 {
                // first half of the frame...
                hub.write_all(header.as_bytes()).await?;
                hub.write_all(&body).await?;
                hub.write_all(&payload[..600]).await?;
                hub.flush().await?;
                // ...window where the read future has partial progress (a real sleep, not
                // yield_now: with 2 workers a yield resumes before the satellite polls mid-frame)
                tokio::time::sleep(std::time::Duration::from_millis(1)).await;
                hub.write_all(&payload[600..]).await?;
            }
            hub.write_all(b"{\"type\":\"audio-stop\",\"data\":{\"timestamp\":0}}\n").await?;
            hub.flush().await?;
            Ok::<(), std::io::Error>(())
        };
        let _ = hub_io.await;
        drop(hub); // clean EOF -> satellite loop should exit Ok

        let result = tokio::time::timeout(std::time::Duration::from_secs(30), sat)
            .await
            .expect("satellite loop hung")
            .unwrap();
        result.expect("connection must survive fragmented frames (no desync error)");
    }

    // THE post-reply re-arm guarantee: audio-stop hands the drain to the playback pump and
    // returns immediately. Blocking here parks the whole select! loop (wake detection, button,
    // mic forwarding) for however long the player takes to drain — ~0.5-2 s after every reply.
    #[tokio::test]
    async fn audio_stop_returns_before_player_drain_completes() {
        let (mut a, _b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (handle, mut done_rx, task) = spawn_pump("cat >/dev/null; sleep 1");
        let mut playback = handle;
        let _pump = AbortOnDrop(task);

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let chunk = WyomingEvent::audio_chunk(22050, 2, 1, vec![0u8; 4410]);
        handle_hub_event(chunk, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        let t0 = std::time::Instant::now();
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert!(
            t0.elapsed() < std::time::Duration::from_millis(500),
            "audio-stop must not block on the player drain (took {:?})",
            t0.elapsed()
        );
        // ...but the drain still completes and is reported, carrying the stream's generation.
        let d = done_rx.recv().await.unwrap();
        assert_eq!(d.generation, playback.latest_generation());
        assert!(d.result.is_ok());
        assert!(t0.elapsed() >= std::time::Duration::from_millis(900), "drain really took the player's time");
    }

    #[tokio::test]
    async fn transcript_while_idle_is_a_noop() {
        let (mut a, b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, _led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, _done_rx, _pump) = pump();
        let e = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(e, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(mode, Mode::Idle);
        // nothing must have been written to the hub
        drop(a);
        let mut buf = tokio::io::BufReader::new(b);
        assert!(crate::wyoming::codec::read_event_buffered(&mut buf).await.unwrap().is_none());
    }

    #[tokio::test]
    async fn turn_lifecycle_publishes_led_states() {
        let (mut a, _b) = tokio::io::duplex(1 << 16);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        let (mut playback, mut done_rx, _pump) = pump();

        start_turn(&mut a, &mut mode, &ctx, &mut preroll, &playback).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        let transcript = WyomingEvent::with_data("transcript", json!({"text":"hi"}));
        handle_hub_event(transcript, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Thinking);

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        // the LED goes Idle when the pump reports the drain complete, not at audio-stop
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    // Announcements: audio-start arrives with no preceding turn and must still light the LED.
    #[tokio::test]
    async fn announcement_playback_publishes_speaking_then_idle() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump();

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Idle);
    }

    // A turn that interrupts an announcement must not go dark when the announcement's
    // audio-stop drains: the LED stays on (Listening) for the rest of the turn.
    #[tokio::test]
    async fn audio_stop_during_streaming_turn_keeps_led_listening() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump();

        // announcement starts, then a button turn begins while it plays
        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let mut preroll: VecDeque<Vec<i16>> = VecDeque::new();
        start_turn(&mut a, &mut mode, &ctx, &mut preroll, &playback).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening);

        // the announcement's audio-stop drains while we are mid-turn
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let d = done_rx.recv().await.unwrap();
        apply_drain_done(d, playback.latest_generation(), mode, &led_tx).unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Listening, "LED must stay lit mid-turn");
    }

    // A stale drain completion (an older stream finishing after a newer audio-start) must not
    // blank the LED mid-Speaking — the generation gate in apply_drain_done.
    #[tokio::test]
    async fn stale_drain_completion_does_not_blank_led() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, mut led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle;
        let (mut playback, mut done_rx, _pump) = pump();

        let start = WyomingEvent::with_data("audio-start", json!({"rate":22050,"width":2,"channels":1}));
        handle_hub_event(start.clone(), &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        let stop = WyomingEvent::with_data("audio-stop", json!({"timestamp":0}));
        handle_hub_event(stop, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        // a second stream starts before the first stream's completion is processed
        handle_hub_event(start, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert_eq!(*led_rx.borrow_and_update(), LedState::Speaking);

        let d = done_rx.recv().await.unwrap();
        assert_eq!(d.generation, 1, "completion belongs to the superseded stream");
        apply_drain_done(d, playback.latest_generation(), mode, &led_tx).unwrap();
        assert!(!led_rx.has_changed().unwrap(), "stale drain must not blank the LED mid-Speaking");
    }

    #[tokio::test]
    async fn stale_transcript_publishes_no_led_state() {
        let (mut a, _b) = tokio::io::duplex(4096);
        let c = cues();

        let (led_tx, led_rx) = watch::channel(LedState::Idle);
        let ctx = Ctx { cues: &c, led: &led_tx };
        let mut mode = Mode::Idle; // not Streaming -> transcript is stale
        let (mut playback, _done_rx, _pump) = pump();

        let stale = WyomingEvent::with_data("transcript", json!({"text":"stale"}));
        handle_hub_event(stale, &mut mode, None, &mut a, &mut playback, &ctx).await.unwrap();
        assert!(!led_rx.has_changed().unwrap(), "stale transcript must not touch the LED");
    }
}