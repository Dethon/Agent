using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

public static class TranscriptionOptionsFactory
{
    // TargetSpeaker: the gate's conclusive identity when present, else the accepted top-1
    // (BestMatch). Any non-Accepted decision leaves it null — extraction has no reliable
    // target for skipped/unavailable verifications, and rejected captures never reach STT.
    public static TranscriptionOptions Create(
        SatelliteConfig config, SpeakerVerification? verification, CaptureStats stats) =>
        new()
        {
            Language = config.Stt?.Wyoming?.Language,
            TargetSpeaker = verification is { Decision: SpeakerDecision.Accepted } v
                ? v.IdentifiedSpeaker ?? v.BestMatch
                : null,
            NoiseFloorRms = stats.FloorRms
        };
}