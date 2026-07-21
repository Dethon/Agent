namespace McpChannelVoice.Settings;

public record SpeakerVerificationSettings
{
    // Master switch. Also effectively off while the voices folder holds no profiles.
    public bool Enabled { get; init; }
    public string ModelPath { get; init; } = "/app/models/speaker-embedding.onnx";
    public string VoicesPath { get; init; } = "/voices";
    // Cosine accept bar, calibrated for ERes2NetV2. Enrollment-domain bands (2026-07-22 offline
    // harness, two enrolled people × all orientations/distances): genuine leave-one-out
    // 0.87-0.94, cross-person (unenrolled-human proxy) max 0.52. Live field bands measured the
    // same night (fran-office, loud TV documentary): far-field TV narration and non-command
    // room chatter that survived endpointing scored 0.38-0.68, a genuine command over the same
    // TV scored 0.86. 0.70 clears the whole measured false-accept band while keeping headroom
    // under live genuine speech. The predecessor CAM++ had NEGATIVE genuine/impostor margin on
    // this hardware (its embeddings tracked the channel, not the speaker) — do not reuse its
    // old 0.45/0.55 calibration. Field-tune per satellite via SatelliteConfig.Verification
    // from the published Similarity telemetry.
    public double SimilarityThreshold { get; init; } = 0.70;
    // Short utterances embed unreliably, so the accept bar ramps linearly with gate-classified
    // speech length: ShortSpeechSimilarityThreshold at/below MinVerifySpeechMs up to
    // SimilarityThreshold at/after FullThresholdSpeechMs. Measured (2026-07-22, enrollment takes
    // sliced + live captures): genuine mean by slice length 0.8s≈0.44, 1.2s≈0.60, 2s≈0.76,
    // 3s≈0.82, 5s≈0.86 while cross-person impostor stays ≤0.46 at every length — so short
    // genuine speech collapses toward the impostor band and a flat full bar rejects real
    // commands (field: genuine 0.599@1.0s and 0.738@0.56s vs TV narration 0.67@13s). The short
    // floor 0.50 still sits above the measured short-impostor band (≤0.41 under 1.2s).
    public double ShortSpeechSimilarityThreshold { get; init; } = 0.50;
    public int FullThresholdSpeechMs { get; init; } = 4000;
    // Below this much gate-classified speech the capture skips verification entirely:
    // sub-second embeddings are unreliable and short real commands must stay safe.
    public int MinVerifySpeechMs { get; init; } = 800;
    // Cosine bar to *name* the speaker (route the enrolled folder name into the message Sender for
    // per-person memory), distinct from — and above — the accept bar. A capture in the
    // [SimilarityThreshold, IdentifyThreshold) band is admitted but stays the generic household
    // identity: attribute to a person only when sure. ERes2NetV2 calibration (2026-07-22): the two
    // enrolled voices score ≤0.52 against each other, genuine 0.87-0.94 — 0.75 names clean speech
    // and abstains in the ambiguous band just above the accept bar. Field-tune per satellite via
    // SatelliteConfig.Verification.
    public double IdentifyThreshold { get; init; } = 0.75;
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