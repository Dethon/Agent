namespace McpChannelVoice.Settings;

public record SpeakerVerificationSettings
{
    // Master switch. Also effectively off while the voices folder holds no profiles.
    public bool Enabled { get; init; }
    public string ModelPath { get; init; } = "/app/models/speaker-embedding.onnx";
    public string VoicesPath { get; init; } = "/voices";
    // Cosine accept bar. Conservative (accept-leaning) until field-tuned; see the spec's
    // calibration notes — the integration test prints real same/cross-speaker scores.
    public double SimilarityThreshold { get; init; } = 0.35;
    // Below this much gate-classified speech the capture skips verification entirely:
    // sub-second embeddings are unreliable and short real commands must stay safe.
    public int MinVerifySpeechMs { get; init; } = 800;
}