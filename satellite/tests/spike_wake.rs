// satellite/tests/spike_wake.rs
// Proves the openWakeWord 3-model ONNX pipeline runs on tract and fires on "ok nabu".
// Pipeline constants (validated 2026-06-09 against openwakeword 0.6.0's own AudioFeatures
// (utils.py) — the algorithm below reproduces its scores to 4 decimals under onnxruntime):
//   - 16 kHz mono int16 PCM, processed in 1280-sample (80 ms) chunks.
//   - melspectrogram.onnx is run over the LAST 1760 samples — the new chunk plus 480 samples
//     (160*3) of lookback carried across chunks (zero-seeded at start). It yields 8 mel
//     frames x 32 bins per chunk; apply transform x/10 + 2. Feed raw int16 values as f32
//     (NOT /32768). (Running mel on isolated 1280-sample chunks loses the analysis-window
//     overlap at chunk boundaries and drops the wake score from ~0.86 to ~0.17 — verified.)
//   - The mel frame buffer is seeded with 76 frames of ONES (mirrors openwakeword); each chunk
//     appends its 8 new frames, then ONE embedding is computed from the LAST 76 mel frames via
//     embedding_model.onnx [1,76,32,1] -> 96-d (an implicit 8-frame / 80 ms hop).
//   - ok_nabu.onnx: input [1,16,96] (the last 16 embeddings), output [1,1] probability
//     (sigmoid baked in); first classification once 16 embeddings exist (~1.3 s in).
use std::collections::VecDeque;
use tract_onnx::prelude::*;

// tract 0.23: into_runnable() yields Arc<RunnableModel<F, O>>; TypedRunnableModel is the
// prelude alias for RunnableModel<TypedFact, Box<dyn TypedOp>> (run() takes self: &Arc<Self>).
type Model = Arc<TypedRunnableModel>;

const CHUNK: usize = 1280; // 80 ms @ 16 kHz
const LOOKBACK: usize = 480; // 160*3 samples of mel context carried across chunks

fn load(bytes: &[u8], shape: &[usize]) -> Model {
    tract_onnx::onnx()
        .model_for_read(&mut std::io::Cursor::new(bytes)).unwrap()
        .with_input_fact(0, f32::fact(shape).into()).unwrap()
        .into_optimized().unwrap()
        .into_runnable().unwrap()
}

fn read_wav_i16(path: &str) -> Vec<i16> {
    let mut r = hound::WavReader::open(path).unwrap();
    assert_eq!(r.spec().sample_rate, 16000);
    assert_eq!(r.spec().channels, 1);
    r.samples::<i16>().map(|s| s.unwrap()).collect()
}

fn max_score(samples: &[i16]) -> f32 {
    let mel = load(include_bytes!("../models/melspectrogram.onnx"), &[1, LOOKBACK + CHUNK]);
    let emb = load(include_bytes!("../models/embedding_model.onnx"), &[1, 76, 32, 1]);
    let clf = load(include_bytes!("../models/ok_nabu.onnx"), &[1, 16, 96]);

    let mut tail = vec![0f32; LOOKBACK]; // lookback carried across chunks, zero-seeded
    let mut mel_buf: VecDeque<[f32; 32]> = (0..76).map(|_| [1f32; 32]).collect(); // ones-init
    let mut emb_buf: VecDeque<[f32; 96]> = VecDeque::new();
    let mut best = 0f32;

    for chunk in samples.chunks_exact(CHUNK) {
        // Stage 1: mel over lookback + chunk -> 8 new frames
        let mut input = vec![0f32; LOOKBACK + CHUNK];
        input[..LOOKBACK].copy_from_slice(&tail);
        for (i, s) in chunk.iter().enumerate() { input[LOOKBACK + i] = *s as f32; }
        tail.copy_from_slice(&input[CHUNK..]); // keep the last 480 samples for the next chunk
        let t: Tensor =
            tract_ndarray::Array2::from_shape_vec((1, LOOKBACK + CHUNK), input).unwrap().into();
        let out = mel.run(tvec!(t.into())).unwrap();
        let view = out[0].to_plain_array_view::<f32>().unwrap();
        let flat: Vec<f32> = view.iter().map(|v| v / 10.0 + 2.0).collect();
        for frame in flat.chunks_exact(32) {
            let mut f = [0f32; 32];
            f.copy_from_slice(frame);
            mel_buf.push_back(f);
            while mel_buf.len() > 76 { mel_buf.pop_front(); } // only the last 76 are ever read
        }
        // Stage 2: ONE embedding from the last 76 mel frames
        let mut w = vec![0f32; 76 * 32];
        for (r, frame) in mel_buf.iter().enumerate() {
            w[r * 32..(r + 1) * 32].copy_from_slice(frame);
        }
        let t: Tensor = tract_ndarray::Array4::from_shape_vec((1, 76, 32, 1), w).unwrap().into();
        let eo = emb.run(tvec!(t.into())).unwrap();
        let ev = eo[0].to_plain_array_view::<f32>().unwrap();
        let mut e = [0f32; 96];
        for (i, v) in ev.iter().take(96).enumerate() { e[i] = *v; }
        emb_buf.push_back(e);
        if emb_buf.len() > 16 { emb_buf.pop_front(); }
        // Stage 3: classify the last 16 embeddings
        if emb_buf.len() == 16 {
            let mut c = vec![0f32; 16 * 96];
            for (i, e) in emb_buf.iter().enumerate() {
                c[i * 96..(i + 1) * 96].copy_from_slice(e);
            }
            let ct: Tensor =
                tract_ndarray::Array3::from_shape_vec((1, 16, 96), c).unwrap().into();
            let co = clf.run(tvec!(ct.into())).unwrap();
            let score = co[0].to_plain_array_view::<f32>().unwrap()[[0, 0]];
            best = best.max(score);
        }
    }
    best
}

#[test]
fn fires_on_ok_nabu() {
    let s = max_score(&read_wav_i16("tests/fixtures/ok_nabu.wav"));
    println!("ok_nabu max score = {s}");
    assert!(s > 0.5, "expected wake score > 0.5, got {s}");
}

#[test]
fn quiet_on_silence() {
    let s = max_score(&read_wav_i16("tests/fixtures/silence.wav"));
    println!("silence max score = {s}");
    assert!(s < 0.5, "expected no wake on silence, got {s}");
}
