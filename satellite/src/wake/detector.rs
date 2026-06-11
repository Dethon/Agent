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

#[derive(Clone)]
pub struct DetectorConfig {
    pub threshold: f32,
    pub trigger_level: u32,
    pub refractory: Duration,
}
impl Default for DetectorConfig {
    fn default() -> Self {
        Self { threshold: 0.5, trigger_level: 1, refractory: Duration::from_secs_f32(2.0) }
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
    pub fn load() -> anyhow::Result<Self> {
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
            clf: load(CLF_MODEL, &[1, CLF_FRAMES, EMB_DIM])?,
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
    cfg: DetectorConfig,
    tail: Vec<f32>,                    // last LOOKBACK samples, zero-seeded
    mel_buf: VecDeque<[f32; 32]>,      // last 76 mel frames, ones-seeded (mirrors openwakeword)
    emb_buf: VecDeque<[f32; EMB_DIM]>, // last 16 embeddings
    activations: u32,
    refractory_until: Option<Instant>,
}

impl WakeDetector {
    pub fn new(models: &WakeModels, cfg: DetectorConfig) -> anyhow::Result<Self> {
        Ok(Self {
            mel: models.mel.clone(),
            emb: models.emb.clone(),
            clf: models.clf.clone(),
            cfg,
            tail: vec![0f32; LOOKBACK],
            mel_buf: (0..MEL_FRAMES).map(|_| [1f32; 32]).collect(),
            emb_buf: VecDeque::new(),
            activations: 0,
            refractory_until: None,
        })
    }

    /// Feed exactly 1280 samples (80 ms). Returns true on a wake event.
    /// The streaming algorithm mirrors openwakeword's AudioFeatures (validated to 4 decimals
    /// against the Python package): mel over lookback+chunk, ones-seeded mel buffer, one
    /// embedding per chunk from the last 76 frames, classify the last 16 embeddings.
    pub fn push_chunk(&mut self, chunk: &[i16]) -> bool {
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
            if self.evaluate(score) { return true; }
        }
        false
    }

    fn evaluate(&mut self, score: f32) -> bool {
        if let Some(until) = self.refractory_until {
            if Instant::now() < until { return false; }
            self.refractory_until = None;
        }
        if score >= self.cfg.threshold {
            self.activations += 1;
            if self.activations >= self.cfg.trigger_level {
                self.activations = 0;
                self.refractory_until = Some(Instant::now() + self.cfg.refractory);
                return true;
            }
        } else {
            self.activations = 0;
        }
        false
    }

    /// Clear streaming state when re-arming after a turn.
    pub fn reset(&mut self) {
        self.tail.fill(0.0);
        self.mel_buf = (0..MEL_FRAMES).map(|_| [1f32; 32]).collect();
        self.emb_buf.clear();
        self.activations = 0;
        self.refractory_until = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn wav(path: &str) -> Vec<i16> {
        let mut r = hound::WavReader::open(path).unwrap();
        r.samples::<i16>().map(|s| s.unwrap()).collect()
    }
    #[test]
    fn fires_once_on_ok_nabu_then_respects_refractory() {
        let models = WakeModels::load().unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/ok_nabu.wav").chunks_exact(1280) {
            if d.push_chunk(chunk) { fires += 1; }
        }
        assert_eq!(fires, 1, "exactly one wake from one utterance");
    }
    #[test]
    fn silent_on_silence() {
        let models = WakeModels::load().unwrap();
        let mut d = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut fires = 0;
        for chunk in wav("tests/fixtures/silence.wav").chunks_exact(1280) {
            if d.push_chunk(chunk) { fires += 1; }
        }
        assert_eq!(fires, 0);
    }
    // Reconnect path: detectors built from ONE loaded bundle must detect independently —
    // shared optimized models, per-detector streaming state.
    #[test]
    fn detectors_share_one_model_bundle() {
        let models = WakeModels::load().unwrap();
        let samples = wav("tests/fixtures/ok_nabu.wav");
        let fires = |d: &mut WakeDetector| samples.chunks_exact(1280).filter(|c| d.push_chunk(c)).count();
        let mut first = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        let mut second = WakeDetector::new(&models, DetectorConfig::default()).unwrap();
        assert_eq!(fires(&mut first), 1);
        assert_eq!(fires(&mut second), 1, "a fresh detector from the shared bundle must behave identically");
    }
}
