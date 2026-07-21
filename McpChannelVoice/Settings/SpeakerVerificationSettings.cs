namespace McpChannelVoice.Settings;

public record SpeakerVerificationSettings
{
    // Master switch. Also effectively off while the voices folder holds no profiles.
    public bool Enabled { get; init; }
    public string ModelPath { get; init; } = "/app/models/speaker-embedding.onnx";
    public string VoicesPath { get; init; } = "/voices";
    // Cosine accept bar. Real CAM++ measurements (integration test): same-speaker ~0.93,
    // cross-speaker ~0.44-0.55 on synthetic voices. Ships at 0.5 — a conservative starting
    // point just above the cross-speaker band; field-tune per satellite from the published
    // Similarity telemetry (per-satellite override via SatelliteConfig.Verification).
    public double SimilarityThreshold { get; init; } = 0.5;
    // Below this much gate-classified speech the capture skips verification entirely:
    // sub-second embeddings are unreliable and short real commands must stay safe.
    public int MinVerifySpeechMs { get; init; } = 800;
}