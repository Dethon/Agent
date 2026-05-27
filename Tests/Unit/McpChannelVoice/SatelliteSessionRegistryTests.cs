using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionRegistryTests
{
    [Fact]
    public void RegisterAndGet_RoundTrips()
    {
        var registry = new SatelliteSessionRegistry();
        var session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        registry.Register(session);

        registry.Get("kitchen-01").ShouldBe(session);
    }

    [Fact]
    public void Get_Unknown_ReturnsNull()
    {
        var registry = new SatelliteSessionRegistry();
        registry.Get("ghost-01").ShouldBeNull();
    }

    [Fact]
    public void Unregister_RemovesEntry()
    {
        var registry = new SatelliteSessionRegistry();
        var session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        registry.Register(session);

        registry.Unregister("kitchen-01");

        registry.Get("kitchen-01").ShouldBeNull();
    }
}