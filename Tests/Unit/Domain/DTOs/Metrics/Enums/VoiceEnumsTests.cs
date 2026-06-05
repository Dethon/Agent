using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class VoiceEnumsTests
{
    [Theory]
    [InlineData("WakeWord")]
    [InlineData("Language")]
    [InlineData("Source")]
    public void VoiceDimension_DoesNotReintroduceRemovedMembers(string removed)
    {
        // These dimensions were always structurally "(unknown)" and were deliberately removed;
        // guard against re-adding them.
        Enum.GetNames<VoiceDimension>().ShouldNotContain(removed);
    }
}