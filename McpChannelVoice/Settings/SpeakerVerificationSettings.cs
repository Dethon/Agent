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
    // Cosine bar to *name* the speaker (route the enrolled folder name into the message Sender for
    // per-person memory), distinct from — and above — the accept bar. A capture in the
    // [SimilarityThreshold, IdentifyThreshold) band is admitted but stays the generic household
    // identity: attribute to a person only when sure. Field data 2026-07-20: enrolled speaker clear
    // commands 0.60-0.92, over-loud-TV lows 0.50-0.60; 0.65 names clean speech and abstains in the
    // ambiguous band. Field-tune per satellite via SatelliteConfig.Verification.
    public double IdentifyThreshold { get; init; } = 0.65;
    // Minimum best-minus-runner-up cosine gap before naming: with two enrolled voices scoring close,
    // naming the top one is a guess, so fall back to household. Auto-satisfied with a single enrolled
    // profile (no runner-up). Conservative default; calibratable only once a second voice is enrolled.
    public double IdentifyMargin { get; init; } = 0.10;
    // Early-close window: a capture still running at this mark is speaker-verified on the audio so
    // far, and an unknown voice (e.g. background TV that latched) is rejected immediately instead
    // of holding the mic open to trailing silence or the max-utterance cap. Enrolled voices and
    // sub-MinVerifySpeechMs captures keep going, so real commands are never truncated. 0 disables.
    public int EarlyVerifyMs { get; init; } = 5000;
}