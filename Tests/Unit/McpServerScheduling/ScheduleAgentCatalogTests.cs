using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleAgentCatalogTests
{
    [Fact]
    public void Catalog_ListsAgentsAndResolvesKnown()
    {
        var catalog = new ScheduleAgentCatalog(new SchedulingSettings
        {
            RedisConnectionString = "x",
            Agents = [new SchedulingAgentConfig { Id = "jonas", Name = "Jonas", Description = "general" }]
        });

        catalog.GetAll().ShouldHaveSingleItem();
        catalog.Exists("jonas").ShouldBeTrue();
        catalog.Exists("ghost").ShouldBeFalse();
        catalog.Get("jonas")!.Name.ShouldBe("Jonas");
    }
}