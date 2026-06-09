using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionRegistryTests
{
    [Fact]
    public void Register_Get_Unregister_Lifecycle()
    {
        var registry = new SatelliteSessionRegistry();
        var session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        registry.Get("kitchen-01").ShouldBeNull();

        registry.Register(session);
        registry.Get("kitchen-01").ShouldBe(session);

        registry.Unregister("kitchen-01");
        registry.Get("kitchen-01").ShouldBeNull();
    }
}