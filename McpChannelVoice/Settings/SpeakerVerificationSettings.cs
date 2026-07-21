namespace McpChannelVoice.Settings;

public record SpeakerVerificationSettings
{
    // Master switch. Also effectively off while the voices folder holds no profiles.
    public bool Enabled { get; init; }
    public string ModelPath { get; init; } = "/app/models/speaker-embedding.onnx";
    public string VoicesPath { get; init; } = "/voices";
    // Cosine accept bar. Field-measured on real far-field hardware (fran-office XVF3800 + loud
    // TV, with the endpointing PeakDropDb=10 and continuous-embedding fixes live): pure TV that
    // survives endpointing scores ~0.38-0.42; the enrolled speaker ~0.50-0.85 (his low end is
    // over loud TV, which mixes into the continuous capture). 0.45 sits in that ~0.08 gap. The
    // earlier 0.6 came from synthetic-TTS fixtures (same-speaker ~0.93) and locked the real
    // speaker out over loud TV. Thin margin, far-field physics — field-tune per satellite via
    // SatelliteConfig.Verification from the published Similarity telemetry.
    public double SimilarityThreshold { get; init; } = 0.45;
    // Below this much gate-classified speech the capture skips verification entirely:
    // sub-second embeddings are unreliable and short real commands must stay safe.
    public int MinVerifySpeechMs { get; init; } = 800;
    // Early-close window: a capture still running at this mark is speaker-verified on the audio so
    // far, and an unknown voice (e.g. background TV that latched) is rejected immediately instead
    // of holding the mic open to trailing silence or the max-utterance cap. Enrolled voices and
    // sub-MinVerifySpeechMs captures keep going, so real commands are never truncated. 0 disables.
    public int EarlyVerifyMs { get; init; } = 5000;
}