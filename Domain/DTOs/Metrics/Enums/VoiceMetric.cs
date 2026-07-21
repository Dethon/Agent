namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis), so values are pinned explicitly: never renumber or
// reuse a value. Reordering/removing a member silently re-labels historical data — removing
// AudioSeconds once shifted every later value and corrupted stored voice metrics. Append new
// members with the next free number. Guarded by VoiceEnumsTests.
public enum VoiceMetric
{
    WakeTriggered = 0,
    UtteranceTranscribed = 1,
    SttLatencyMs = 2,
    TtsLatencyMs = 3,
    WakeToFirstAudioMs = 4,
    ApprovalResolved = 5,
    SttError = 6,
    TtsError = 7,
    AnnouncePlayed = 8,
    AnnounceQueued = 9,
    AnnounceError = 10,
    AnnouncePreemptedReply = 11,
    FollowUpWindowOpened = 12,
    FollowUpEngaged = 13,
    FollowUpTimedOut = 14,
    AlarmAcknowledged = 15,
    AlarmUnacknowledged = 16,
    AlarmOffline = 17,
    UtteranceRejected = 18
}