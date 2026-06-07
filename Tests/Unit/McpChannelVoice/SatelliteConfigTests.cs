using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteConfigTests
{
    [Fact]
    public void DisplayLocation_NoLocality_ReturnsRoom()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };

        config.DisplayLocation.ShouldBe("Kitchen");
    }

    [Fact]
    public void DisplayLocation_BlankLocality_ReturnsRoom()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen", Locality = "   " };

        config.DisplayLocation.ShouldBe("Kitchen");
    }

    [Fact]
    public void DisplayLocation_WithLocality_AppendsLocalityInParens()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Locality = "Madrid, Spain"
        };

        config.DisplayLocation.ShouldBe("Kitchen (Madrid, Spain)");
    }
}