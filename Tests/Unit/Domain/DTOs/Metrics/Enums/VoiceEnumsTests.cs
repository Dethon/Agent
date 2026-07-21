using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class VoiceEnumsTests
{
    [Theory]
    [InlineData("WakeWord")]
    [InlineData("Language")]
    [InlineData("Source")]
    [InlineData("SttProvider")]
    [InlineData("SttModel")]
    [InlineData("TtsProvider")]
    [InlineData("TtsVoice")]
    public void VoiceDimension_DoesNotReintroduceRemovedMembers(string removed)
    {
        // These dimensions were always structurally "(unknown)" — never populated on any VoiceEvent —
        // and were deliberately removed; guard against re-adding them.
        Enum.GetNames<VoiceDimension>().ShouldNotContain(removed);
    }

    [Theory]
    [InlineData("AudioSeconds")]
    public void VoiceMetric_DoesNotReintroduceRemovedMembers(string removed)
    {
        // AudioSeconds was defined but never published anywhere; removed to keep the enum honest.
        Enum.GetNames<VoiceMetric>().ShouldNotContain(removed);
    }

    // VoiceMetric/VoiceDimension are persisted as integers in Redis metric events, so their numeric
    // values are part of the wire format. Renumbering re-labels historical data (removing AudioSeconds
    // once shifted every later value). These guards pin the contract: only ever append new members.
    [Theory]
    [InlineData(VoiceMetric.WakeTriggered, 0)]
    [InlineData(VoiceMetric.UtteranceTranscribed, 1)]
    [InlineData(VoiceMetric.SttLatencyMs, 2)]
    [InlineData(VoiceMetric.TtsLatencyMs, 3)]
    [InlineData(VoiceMetric.WakeToFirstAudioMs, 4)]
    [InlineData(VoiceMetric.ApprovalResolved, 5)]
    [InlineData(VoiceMetric.SttError, 6)]
    [InlineData(VoiceMetric.TtsError, 7)]
    [InlineData(VoiceMetric.AnnouncePlayed, 8)]
    [InlineData(VoiceMetric.AnnounceQueued, 9)]
    [InlineData(VoiceMetric.AnnounceError, 10)]
    [InlineData(VoiceMetric.AnnouncePreemptedReply, 11)]
    [InlineData(VoiceMetric.FollowUpWindowOpened, 12)]
    [InlineData(VoiceMetric.FollowUpEngaged, 13)]
    [InlineData(VoiceMetric.FollowUpTimedOut, 14)]
    [InlineData(VoiceMetric.AlarmAcknowledged, 15)]
    [InlineData(VoiceMetric.AlarmUnacknowledged, 16)]
    [InlineData(VoiceMetric.AlarmOffline, 17)]
    [InlineData(VoiceMetric.UtteranceRejected, 18)]
    public void VoiceMetric_HasPinnedWireValues(VoiceMetric metric, int expected) =>
        ((int)metric).ShouldBe(expected);

    [Theory]
    [InlineData(VoiceDimension.SatelliteId, 0)]
    [InlineData(VoiceDimension.Room, 1)]
    [InlineData(VoiceDimension.Identity, 2)]
    [InlineData(VoiceDimension.Outcome, 3)]
    [InlineData(VoiceDimension.Priority, 4)]
    public void VoiceDimension_HasPinnedWireValues(VoiceDimension dimension, int expected) =>
        ((int)dimension).ShouldBe(expected);
}