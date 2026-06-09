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
}