// satellite/tests/tract_clf_compat.rs
// Gate for the wake-model retrain (spec 2026-07-17-wake-word-reliability): proves tract 0.23
// can load + run every classifier candidate in tests/fixtures/clf/ under the satellite's
// exact contract ([1,16,96] f32 -> [1,1] f32). livekit-wakeword's conv_attention export is
// opset 18 with fused LayerNormalization and Shape/Gather/Slice chains from the decomposed
// nn.MultiheadAttention — exactly the ops this test probes. Temporary: removed once the
// trained model ships embedded (spike_wake.rs then covers the same ops via include_bytes!).
use tract_onnx::prelude::*;

#[test]
fn classifier_candidates_load_and_run_under_tract() {
    let dir = std::path::Path::new("tests/fixtures/clf");
    let candidates: Vec<_> = std::fs::read_dir(dir)
        .expect("tests/fixtures/clf missing")
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "onnx"))
        .collect();
    assert!(!candidates.is_empty(), "no candidate .onnx files in tests/fixtures/clf");
    for path in candidates {
        let model = tract_onnx::onnx()
            .model_for_path(&path)
            .unwrap_or_else(|e| panic!("{path:?}: parse failed: {e}"))
            .with_input_fact(0, f32::fact(&[1, 16, 96]).into())
            .unwrap()
            .into_optimized()
            .unwrap_or_else(|e| panic!("{path:?}: optimize failed: {e}"))
            .into_runnable()
            .unwrap_or_else(|e| panic!("{path:?}: runnable failed: {e}"));
        let input: Tensor = tract_ndarray::Array3::<f32>::zeros((1, 16, 96)).into();
        let out = model
            .run(tvec!(input.into()))
            .unwrap_or_else(|e| panic!("{path:?}: run failed: {e}"));
        let score = out[0].to_plain_array_view::<f32>().unwrap()[[0, 0]];
        assert!(score.is_finite(), "{path:?}: non-finite score {score}");
        println!("{}: OK, zeros-input score = {score}", path.display());
    }
}
