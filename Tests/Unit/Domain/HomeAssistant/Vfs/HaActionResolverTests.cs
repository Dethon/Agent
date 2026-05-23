using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaActionResolverTests
{
    private static readonly List<HaServiceDefinition> Services =
    [
        Service("light", "turn_on", AnyEntityTarget()),
        Service("light", "toggle", DomainTarget("light")),
        Service("light", "no_target", null),                 // not entity-targeted
        Service("vacuum", "start", DomainTarget("vacuum")),  // wrong class domain
        Service("homeassistant", "restart", null)
    ];

    [Fact]
    public void ServicesFor_ReturnsClassDomainTargetedServices_Sorted()
    {
        var result = HaActionResolver.ServicesFor("light.kitchen", Services)
            .Select(s => s.Service).ToList();
        result.ShouldBe(["toggle", "turn_on"]);
    }

    [Fact]
    public void ServicesFor_DomainNarrowedToOtherClass_Excluded()
    {
        HaActionResolver.ServicesFor("light.kitchen", Services)
            .ShouldNotContain(s => s.Service == "start");
    }

    [Fact]
    public void ServicesFor_ReadOnlyEntity_ReturnsEmpty()
    {
        // sensor has no class-domain entity-targeted services here.
        HaActionResolver.ServicesFor("sensor.salon_temp", Services).ShouldBeEmpty();
    }
}