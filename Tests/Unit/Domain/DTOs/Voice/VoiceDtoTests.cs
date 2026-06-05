using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Voice;

public class VoiceDtoTests
{
    [Fact]
    public void AudioFormat_DefaultsToWyomingStandard()
    {
        var fmt = AudioFormat.WyomingStandard;
        fmt.SampleRateHz.ShouldBe(16_000);
        fmt.SampleWidthBytes.ShouldBe(2);
        fmt.Channels.ShouldBe(1);
    }
}