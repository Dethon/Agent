namespace Domain.DTOs.Metrics.Enums;

public enum VoiceMetric
{
    WakeTriggered,
    UtteranceTranscribed,
    AudioSeconds,
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