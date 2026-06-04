using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class VoiceEnumsTests
{
    [Fact]
    public void VoiceDimension_HasExpectedMembers()
    {
        var names = Enum.GetNames<VoiceDimension>();
        names.ShouldContain(nameof(VoiceDimension.SatelliteId));
        names.ShouldContain(nameof(VoiceDimension.Room));
        names.ShouldContain(nameof(VoiceDimension.Identity));
        names.ShouldContain(nameof(VoiceDimension.SttProvider));
        names.ShouldContain(nameof(VoiceDimension.SttModel));
        names.ShouldContain(nameof(VoiceDimension.TtsProvider));
        names.ShouldContain(nameof(VoiceDimension.TtsVoice));
        names.ShouldContain(nameof(VoiceDimension.Outcome));
        names.ShouldContain(nameof(VoiceDimension.Priority));

        // WakeWord, Language and Source were always structurally "(unknown)" and were removed.
        names.ShouldNotContain("WakeWord");
        names.ShouldNotContain("Language");
        names.ShouldNotContain("Source");
    }

    [Fact]
    public void VoiceMetric_HasExpectedMembers()
    {
        var names = Enum.GetNames<VoiceMetric>();
        names.ShouldContain(nameof(VoiceMetric.WakeTriggered));
        names.ShouldContain(nameof(VoiceMetric.UtteranceTranscribed));
        names.ShouldContain(nameof(VoiceMetric.AudioSeconds));
        names.ShouldContain(nameof(VoiceMetric.SttLatencyMs));
        names.ShouldContain(nameof(VoiceMetric.TtsLatencyMs));
        names.ShouldContain(nameof(VoiceMetric.WakeToFirstAudioMs));
        names.ShouldContain(nameof(VoiceMetric.ApprovalResolved));
        names.ShouldContain(nameof(VoiceMetric.SttError));
        names.ShouldContain(nameof(VoiceMetric.TtsError));
        names.ShouldContain(nameof(VoiceMetric.AnnouncePlayed));
        names.ShouldContain(nameof(VoiceMetric.AnnounceQueued));
        names.ShouldContain(nameof(VoiceMetric.AnnounceError));
        names.ShouldContain(nameof(VoiceMetric.AnnouncePreemptedReply));
    }
}