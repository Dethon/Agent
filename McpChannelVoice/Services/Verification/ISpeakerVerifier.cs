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

public readonly record struct SpeakerVerification(
    SpeakerDecision Decision, double? Similarity = null, string? BestMatch = null);

public interface ISpeakerVerifier
{
    // speechAudio: the capture's speech-classified chunks; speechMs the gate's speech
    // total. Skipped (disabled / too short) and Unavailable (no model, no profiles,
    // inference failure) both mean "let the capture through" — only Rejected blocks.
    Task<SpeakerVerification> VerifyAsync(
        IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct);
}