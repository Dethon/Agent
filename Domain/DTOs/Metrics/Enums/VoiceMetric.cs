namespace Domain.DTOs.Metrics.Enums;

public enum VoiceMetric
{
    WakeTriggered,
    UtteranceTranscribed,
    SttLatencyMs,
    TtsLatencyMs,
    WakeToFirstAudioMs,
    ApprovalResolved,
    SttError,
    TtsError,
    AnnouncePlayed,
    AnnounceQueued,
    AnnounceError,
    AnnouncePreemptedReply
}