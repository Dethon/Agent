using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Verification;

public enum SpeakerDecision
{
    Accepted,
    Rejected,
    Skipped,
    Unavailable
}

// IdentifiedSpeaker is the enrolled profile name to attribute this capture to (routed into the
// message Sender for per-person memory), set only when the match is *conclusive* — Accepted, past
// IdentifyThreshold, and clear of the runner-up by IdentifyMargin. Null (doubtful / skipped /
// unavailable / rejected) means fall back to the satellite's default identity. BestMatch is the raw
// top match regardless of the identify policy (telemetry); the two differ in the doubtful band.
public readonly record struct SpeakerVerification(
    SpeakerDecision Decision, double? Similarity = null, string? BestMatch = null,
    string? IdentifiedSpeaker = null);

public interface ISpeakerVerifier
{
    // captureAudio: the full continuous capture (enrollment-matching — embedding silence-cut
    // speech-only fragments collapses similarity); speechMs the gate's speech total, used only
    // for the short-utterance skip. enforceMinSpeech=false opts out of that skip for the
    // early-close check (a still-running capture is not a short command and has ample continuous
    // audio to judge). Skipped (disabled / too short) and Unavailable (no model, no profiles,
    // inference failure) both mean "let the capture through" — only Rejected blocks.
    Task<SpeakerVerification> VerifyAsync(
        IReadOnlyList<AudioChunk> captureAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
        bool enforceMinSpeech = true);
}