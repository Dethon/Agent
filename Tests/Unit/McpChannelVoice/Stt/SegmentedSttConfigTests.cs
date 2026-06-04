using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class SegmentedSttConfigTests
{
    [Fact]
    public void Defaults_AreConservativeAndDisabled()
    {
        var config = new SegmentedSttConfig();

        config.Enabled.ShouldBeFalse();
        config.SilenceRmsThreshold.ShouldBe(500);
        config.SegmentSilenceMs.ShouldBe(350);
        config.MinSegmentMs.ShouldBe(800);
        config.MaxInFlightDecodes.ShouldBe(1);
        config.FinalReconcile.ShouldBeFalse();
    }

    [Fact]
    public void SttSettings_ExposesStreamingWithNonNullDefault()
    {
        new SttSettings().Streaming.ShouldNotBeNull();
    }
}