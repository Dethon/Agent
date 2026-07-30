use std::collections::VecDeque;
use std::time::{Duration, Instant};
use tract_onnx::prelude::*;

// tract 0.23: into_runnable() yields Arc<RunnableModel<F, O>>; TypedRunnableModel is the
// prelude alias for RunnableModel<TypedFact, Box<dyn TypedOp>> — run() takes &Arc<Self>.
type Model = Arc<TypedRunnableModel>;

const MEL_MODEL: &[u8] = include_bytes!("../../models/melspectrogram.onnx");
const EMB_MODEL: &[u8] = include_bytes!("../../models/embedding_model.onnx");
const CLF_MODEL: &[u8] = include_bytes!("../../models/ok_nabu.onnx");

const CHUNK: usize = 1280;    // 80 ms @ 16 kHz
const LOOKBACK: usize = 480;  // 160*3 samples of mel context carried across chunks (openwakeword)
const MEL_FRAMES: usize = 76; // embedding window: the last 76 mel frames
const EMB_DIM: usize = 96;
const CLF_FRAMES: usize = 16; // classifier window: the last 16 embeddings

/// Root-mean-square level of a chunk of i16 PCM samples, in i16-amplitude units (0..=32768).
/// Diagnostic only: lets the wake path log how loud each chunk is (silence vs speech vs music)
/// so detector tuning (threshold, window, gain) is driven by real on-device levels.
pub fn chunk_rms(samples: &[i16]) -> f32 {
    if samples.is_empty() { return 0.0; }
    let sum_sq: f64 = samples.iter().map(|&s| s as f64 * s as f64).sum();
    (sum_sq / samples.len() as f64).sqrt() as f32
}

#[derive(Clone)]
pub struct DetectorConfig {
    pub threshold: f32,
    /// How many consecutive classifier scores are averaged before the mean is compared against
    /// `threshold`. 1 compares each score directly. Raising it buys noise immunity at one extra
    /// 80 ms frame of wake latency per step — past 3 the total exceeds `wake_preroll_ms`'s 240 ms
    /// gap flush, so bump that too.
    pub window: u32,
    pub refractory: Duration,
}
impl Default for DetectorConfig {
    fn default() -> Self {
        Self { threshold: 0.5, window: 1, refractory: Duration::from_secs_f32(2.0) }
    }
}

/// The wake decision rule: a sliding mean of the last `window` classifier scores, fired when
/// that mean reaches `threshold`.
///
/// This replaced a consecutive-frame counter that reset to zero on any single frame below
/// threshold. With a jittery score trace — which is what background TV or music produces — one
/// dip between two strong frames threw the whole utterance away, so the satellite would not
/// wake even when the phrase was clearly spoken. Averaging rides through the dip. It is also
/// what microWakeWord/ESPHome does, and it subsumes the old `trigger_level` knob: `window` 1 is
/// the old level 1, and `window` n is strictly more permissive than the old level n (that
/// required every frame at or above threshold; the mean only needs them to average it).
///
/// Deliberately model-free so the rule is testable with plain numbers, no ONNX inference.
struct WindowTrigger {
    scores: VecDeque<f32>,
    window: usize,
    threshold: f32,
    refractory: Duration,
    refractory_until: Option<Instant>,
}

impl WindowTrigger {
    fn new(cfg: &DetectorConfig) -> Self {
        Self {
            scores: VecDeque::with_capacity(cfg.window as usize),
            window: (cfg.window as usize).max(1),
            threshold: cfg.threshold,
            refractory: cfg.refractory,
            refractory_until: None,
        }
    }

    /// Feed one classifier score. Returns `(fired, mean)`, where `mean` is over the scores
    /// currently buffered — diagnostic only, and 0.0 while the refractory is suppressing input.
    ///
    /// A partial window never fires: after a fire or a reset the next `window - 1` frames carry
    /// no decision. Firing clears the buffer, so the stale high scores that produced it cannot
    /// immediately re-fire once the refractory expires.
    fn push(&mut self, score: f32) -> (bool, f32) {
        if let Some(until) = self.refractory_until {
            if Instant::now() < until { return (false, 0.0); }
            self.refractory_until = None;
        }
        self.scores.push_back(score);
        while self.scores.len() > self.window { self.scores.pop_front(); }
        let mean = self.scores.iter().sum::<f32>() / self.scores.len() as f32;
        if self.scores.len() < self.window { return (false, mean); }
        if mean >= self.threshold {
            self.scores.clear();
            self.refractory_until = Some(Instant::now() + self.refractory);
            return (true, mean);
        }
        (false, mean)
    }

    fn reset(&mut self) {
        self.scores.clear();
        self.refractory_until = None;
    }
}

/// The three openwakeword ONNX models, parsed + graph-optimized ONCE per process: optimization
/// costs real time on an A53, and the hub reconnects forever — per-connection detectors built
/// from a loaded bundle only clone Arcs and seed buffers, so re-arm after a reconnect is instant.
#[derive(Clone)]
pub struct WakeModels {
    mel: Model,
    emb: Model,
    clf: Model,
}

impl WakeModels {
    pub fn load() -> anyhow::Result<Self> { Self::load_with_classifier(CLF_MODEL) }

    /// Classifier bytes are a parameter so tests can pair the stock fixture wav with the
    /// stock classifier it was validated against, independent of whichever classifier
    /// (custom-trained or otherwise) `load()` ships in production.
    pub fn load_with_classifier(classifier: &[u8]) -> anyhow::Result<Self> {
        let load = |b: &[u8], shape: &[usize]| -> anyhow::Result<Model> {
            tract_onnx::onnx()
                .model_for_read(&mut std::io::Cursor::new(b))?
                .with_input_fact(0, f32::fact(shape).into())?
                .into_optimized()?
                .into_runnable()
        };
        Ok(Self {
            mel: load(MEL_MODEL, &[1, LOOKBACK + CHUNK])?,
            emb: load(EMB_MODEL, &[1, MEL_FRAMES, 32, 1])?,
            clf: load(classifier, &[1, CLF_FRAMES, EMB_DIM])?,
        })
    }
}

pub struct WakeDetector {
    // NOTE: these stay Arc<TypedRunnableModel> and pay Model::run()'s per-call SimpleState
    // spawn. Holding persistent SimpleStates would save that allocation but SimpleState is
    // !Send (Box<dyn OpState> has no Send bound), and the detector lives across awaits in a
    // tokio::spawn'd connection task.
    mel: Model,
    emb: Model,
    clf: Model,
    tail: Vec<f32>,                    // last LOOKBACK samples, zero-seeded
    mel_buf: VecDeque<[f32; 32]>,      // last 76 mel frames, ones-seeded (mirrors openwakeword)
    emb_buf: VecDeque<[f32; EMB_DIM]>, // last 16 embeddings
    trigger: WindowTrigger,
}

impl WakeDetector {
    pub fn new(models: &WakeModels, cfg: DetectorConfig) -> anyhow::Result<Self> {
        Ok(Self {
            mel: models.mel.clone(),
            emb: models.emb.clone(),
            clf: models.clf.clone(),
            tail: vec![0f32; LOOKBACK],
            mel_buf: (0..MEL_FRAMES).map(|_| [1f32; 32]).collect(),
            emb_buf: VecDeque::new(),
            trigger: WindowTrigger::new(&cfg),
        })
    }

    /// Feed exactly 1280 samples (80 ms). Returns the classifier score when a wake fires.
    /// The streaming algorithm mirrors openwakeword's AudioFeatures (validated to 4 decimals
    /// against the Python package): mel over lookback+chunk, ones-seeded mel buffer, one
    /// embedding per chunk from the last 76 frames, classify the last 16 embeddings.
    pub fn push_chunk(&mut self, chunk: &[i16]) -> Option<f32> {
        assert_eq!(chunk.len(), CHUNK, "push_chunk requires exactly {CHUNK} samples");
        // Stage 1: mel over lookback + chunk -> 8 new frames (x/10 + 2)
        let mut input = vec![0f32; LOOKBACK + CHUNK];
        input[..LOOKBACK].copy_from_slice(&self.tail);
        for (i, s) in chunk.iter().enumerate() { input[LOOKBACK + i] = *s as f32; }
        self.tail.copy_from_slice(&input[CHUNK..]); // keep the last 480 samples for the next chunk
        let t: Tensor =
            tract_ndarray::Array2::from_shape_vec((1, LOOKBACK + CHUNK), input).unwrap().into();
        let out = self.mel.run(tvec!(t.into())).expect("mel run");
        let flat: Vec<f32> = out[0].to_plain_array_view::<f32>().unwrap().iter().map(|v| v / 10.0 + 2.0).collect();
        for frame in flat.chunks_exact(32) {
            let mut f = [0f32; 32];
            f.copy_from_slice(frame);
            self.mel_buf.push_back(f);
            while self.mel_buf.len() > MEL_FRAMES { self.mel_buf.pop_front(); }
        }
        // Stage 2: ONE embedding from the last 76 mel frames (implicit 8-frame / 80 ms hop)
        let mut w = vec![0f32; MEL_FRAMES * 32];
        for (r, frame) in self.mel_buf.iter().enumerate() {
            w[r * 32..(r + 1) * 32].copy_from_slice(frame);
        }
        let t: Tensor =
            tract_ndarray::Array4::from_shape_vec((1, MEL_FRAMES, 32, 1), w).unwrap().into();
        let eo = self.emb.run(tvec!(t.into())).expect("emb run");
        let ev = eo[0].to_plain_array_view::<f32>().unwrap();
        let mut e = [0f32; EMB_DIM];
        for (i, v) in ev.iter().take(EMB_DIM).enumerate() { e[i] = *v; }
        self.emb_buf.push_back(e);
        if self.emb_buf.len() > CLF_FRAMES { self.emb_buf.pop_front(); }
        // Stage 3: classify the last 16 embeddings
        if self.emb_buf.len() == CLF_FRAMES {
            let mut c = vec![0f32; CLF_FRAMES * EMB_DIM];
            for (i, em) in self.emb_buf.iter().enumerate() {
                c[i * EMB_DIM..(i + 1) * EMB_DIM].copy_from_slice(em);
            }
            let ct: Tensor =
                tract_ndarray::Array3::from_shape_vec((1, CLF_FRAMES, EMB_DIM), c).unwrap().into();
            let co = self.clf.run(tvec!(ct.into())).expect("clf run");
            let score = co[0].to_plain_array_view::<f32>().unwrap()[[0, 0]];
            // The trigger must see EVERY frame, so push before the log gate below.
            let (fired, mean) = self.trigger.push(score);
            // Diagnostic (tuning): per-chunk wake score, the window mean the rule actually acts
            // on, and mic level — for setting threshold / window from real on-device data. At
            // debug so it's silent in production (RUST_LOG=info) but available via
            // RUST_LOG=...=debug when tuning; gated at a low score floor so even at debug idle
            // silence doesn't flood, and rms (and the macro fields) are only evaluated when the
            // level is enabled — the steady path pays nothing.
            if score >= 0.05 { tracing::debug!(score, mean, rms = chunk_rms(chunk), "wake score"); }
            if fired { return Some(score); }
        }
        None
    }

    /// Clear streaming state when re-arming after a turn.
    pub fn reset(&mut self) {
        self.tail.fill(0.0);
        self.mel_buf = (0..MEL_FRAMES).map(|_| [1f32; 32]).collect();
        self.emb_buf.clear();
        self.trigger.reset();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    // The shipped models/ok_nabu.onnx is a custom-trained classifier; tests/fixtures/ok_nabu.wav
    // is the stock recording validated against the stock classifier (77db1daa). Detector
    // plumbing — refractory, shared bundle, score surfacing — is what these tests cover, so
    // they pair the stock model with the stock wav.
    const STOCK_CLF_MODEL: &[u8] = include_bytes!("../../tests/fixtures/ok_nabu_stock.onnx");
    fn wav(path: &str) -> Vec<i16> {
        let mut r = hound::WavReader::open(path).unwrap();
        r.samples::<i16>().map(|s| s.unwrap()).collect()
    }
    #[test]
    fn fires_once_on_ok_nabu_then_respects_refractory() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/ok_nabu.wav").chunks_exact(1280) {
            if d.push_chunk(chunk).is_some() { fires += 1; }
        }
        assert_eq!(fires, 1, "exactly one wake from one utterance");
    }
    #[test]
    fn silent_on_silence() {
        let models = WakeModels::load().unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/silence.wav").chunks_exact(1280) {
            if d.push_chunk(chunk).is_some() { fires += 1; }
        }
        assert_eq!(fires, 0);
    }
    // Reconnect path: detectors built from ONE loaded bundle must detect independently —
    // shared optimized models, per-detector streaming state.
    #[test]
    fn detectors_share_one_model_bundle() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let samples = wav("tests/fixtures/ok_nabu.wav");
        let fires = |d: &mut WakeDetector| samples.chunks_exact(1280).filter(|c| d.push_chunk(c).is_some()).count();
        let mut first = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut second = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        assert_eq!(fires(&mut first), 1);
        assert_eq!(fires(&mut second), 1, "a fresh detector from the shared bundle must behave identically");
    }

    #[test]
    fn chunk_rms_matches_known_values() {
        assert_eq!(chunk_rms(&[]), 0.0);
        assert_eq!(chunk_rms(&[100, -100, 100, -100]), 100.0); // constant magnitude -> rms == magnitude
        let r = chunk_rms(&[3, 4]);
        assert!((r - 12.5f32.sqrt()).abs() < 1e-3, "rms([3,4]) = sqrt((9+16)/2) = sqrt(12.5), got {r}");
    }

    #[test]
    fn push_chunk_reports_score_on_wake() {
        let models = WakeModels::load_with_classifier(STOCK_CLF_MODEL).unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let scores: Vec<f32> = wav("tests/fixtures/ok_nabu.wav")
            .chunks_exact(1280)
            .filter_map(|c| d.push_chunk(c))
            .collect();
        assert_eq!(scores.len(), 1, "exactly one wake from one utterance");
        assert!(scores[0] >= 0.5, "winning score is at least the threshold, got {}", scores[0]);
    }

    // ---- WindowTrigger: the decision rule, tested with plain numbers (no ONNX inference) ----

    fn trigger(window: u32, threshold: f32, refractory_ms: u64) -> WindowTrigger {
        WindowTrigger::new(&DetectorConfig {
            threshold,
            window,
            refractory: Duration::from_millis(refractory_ms),
        })
    }

    /// The whole point of the change: the old consecutive-frame counter reset on any single
    /// frame below threshold, so a jittery score trace in noise threw the utterance away.
    #[test]
    fn window_mean_fires_through_a_single_frame_dip() {
        let mut t = trigger(3, 0.7, 0);
        assert!(!t.push(0.75).0);
        assert!(!t.push(0.68).0, "second frame dips below threshold");
        assert!(t.push(0.78).0, "mean 0.7367 clears 0.7 despite the dip");
    }

    #[test]
    fn window_mean_stays_silent_on_a_plateau_below_threshold() {
        let mut t = trigger(3, 0.7, 0);
        for s in [0.60, 0.62, 0.64, 0.63, 0.61] {
            assert!(!t.push(s).0, "mean never reaches 0.7, so {s} must not fire");
        }
    }

    /// A partial window must never fire: after a reset the first window-1 frames carry no
    /// decision, exactly as trigger_level 2 cost one frame of deadness before.
    #[test]
    fn partial_window_never_fires() {
        let mut t = trigger(3, 0.7, 0);
        assert!(!t.push(0.99).0);
        assert!(!t.push(0.99).0);
        assert!(t.push(0.99).0, "fires only once the window is full");
    }

    /// Firing clears the buffer. Without this the stale high scores still in it would re-fire
    /// on the very next frame once the refractory expired.
    #[test]
    fn firing_clears_the_window() {
        let mut t = trigger(3, 0.7, 0);
        let fires = (0..6).filter(|_| t.push(0.8).0).count();
        assert_eq!(fires, 2, "6 frames at window 3 = 2 fires, not 4");
    }

    #[test]
    fn refractory_suppresses_then_expires() {
        let mut t = trigger(1, 0.7, 40);
        assert!(t.push(0.9).0, "first frame fires");
        assert!(!t.push(0.9).0, "second is inside the refractory");
        std::thread::sleep(Duration::from_millis(60));
        assert!(t.push(0.9).0, "fires again once the refractory has expired");
    }

    /// window 1 is the default and must behave exactly like the old trigger_level 1: the
    /// instantaneous score compared straight against the threshold.
    #[test]
    fn window_of_one_fires_on_any_frame_at_threshold() {
        let mut t = trigger(1, 0.7, 0);
        assert!(t.push(0.8).0);
        assert!(!t.push(0.6).0);
        assert!(t.push(0.71).0);
    }

    #[test]
    fn reset_clears_window_and_refractory() {
        let mut t = trigger(3, 0.7, 60_000);
        assert!(!t.push(0.8).0);
        assert!(!t.push(0.8).0);
        t.reset();
        assert!(!t.push(0.8).0, "reset dropped the two buffered frames");
        assert!(!t.push(0.8).0);
        assert!(t.push(0.8).0, "a fresh full window fires");
        t.reset();
        assert!(!t.push(0.8).0, "reset also cleared the 60 s refractory");
        assert!(!t.push(0.8).0);
        assert!(t.push(0.8).0, "so a full window fires instead of being suppressed");
    }

    #[test]
    fn push_reports_the_mean_it_evaluated() {
        let mut t = trigger(2, 0.9, 0);
        assert_eq!(t.push(0.4).1, 0.4, "partial window reports what it has");
        let (fired, mean) = t.push(0.6);
        assert!(!fired);
        assert!((mean - 0.5).abs() < 1e-6, "mean of 0.4 and 0.6, got {mean}");
    }
}
