using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteConfigTests
{
    [Theory]
    [InlineData(null, "Kitchen")]
    [InlineData("   ", "Kitchen")]
    [InlineData("Madrid, Spain", "Kitchen (Madrid, Spain)")]
    public void DisplayLocation_AppendsLocalityWhenPresent(string? locality, string expected)
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen", Locality = locality };

        config.DisplayLocation.ShouldBe(expected);
    }

    [Fact]
    public void ResolveGateThresholds_NoOverride_UsesGlobals()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(700);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }

    [Fact]
    public void ResolveGateThresholds_WithOverride_UsesSatelliteValues()
    {
        // Mic front-ends differ (e.g. XVF3800 AGC raises the noise floor), so one global RMS
        // bar can't fit every satellite.
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900, MinSpeechMs = 400 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(400);
    }

    [Fact]
    public void ResolveGateThresholds_PartialOverride_FallsBackPerField()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }
}