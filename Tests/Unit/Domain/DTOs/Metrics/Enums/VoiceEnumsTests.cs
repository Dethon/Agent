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
}