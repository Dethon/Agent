using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteRegistryTests
{
    private static readonly Dictionary<string, SatelliteConfig> _sample = new()
    {
        ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" },
        ["living-room-01"] = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" },
        ["bedroom-01"] = new() { Identity = "francisco", Room = "Bedroom", WakeWord = "hey_jarvis" }
    };

    [Fact]
    public void GetById_KnownSatellite_ReturnsConfig()
    {
        var registry = new SatelliteRegistry(_sample);
        var sat = registry.GetById("kitchen-01");
        sat.ShouldNotBeNull();
        sat!.Identity.ShouldBe("household");
        sat.Room.ShouldBe("Kitchen");
    }

    [Fact]
    public void GetById_UnknownSatellite_ReturnsNull()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetById("ghost-01").ShouldBeNull();
    }

    [Fact]
    public void GetIdsByRoom_MatchesCaseInsensitive()
    {
        var registry = new SatelliteRegistry(_sample);
        var ids = registry.GetIdsByRoom("kitchen");
        ids.ShouldBe(["kitchen-01"]);
    }

    [Fact]
    public void GetIdsByRoom_UnknownRoom_ReturnsEmpty()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetIdsByRoom("Basement").ShouldBeEmpty();
    }

    [Fact]
    public void GetAllIds_ReturnsEverySatellite()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetAllIds().ShouldBe(["kitchen-01", "living-room-01", "bedroom-01"], ignoreOrder: true);
    }
}