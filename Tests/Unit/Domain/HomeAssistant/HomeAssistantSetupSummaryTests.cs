using System.Text.Json.Nodes;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Domain.HomeAssistant.Vfs;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeAssistantSetupSummaryTests
{
    private static HomeAssistantSetupSummary Build(FakeHaClient client) =>
        new(new HaCatalogProvider(() => client, new FakeTimeProvider()));

    [Fact]
    public async Task GetAsync_RendersBothTreesAsPaths()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp"))),
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["sensor.salon_temp"]}]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("## Current Home Assistant setup");
        text.ShouldContain("Mounted at `/ha`");
        text.ShouldContain("/ha/areas/salon/sensor.salon_temp_(salon-temp)");
        text.ShouldContain("/ha/areas/unassigned/light.kitchen_(kitchen)");
        text.ShouldContain("/ha/entities/light/kitchen_(kitchen)");
        text.ShouldContain("/ha/entities/sensor/salon_temp_(salon-temp)");
    }

    [Fact]
    public async Task GetAsync_PathsAreLexicallySorted()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.b_lamp", "off"),
                Entity("light.a_lamp", "off"),
            },
            AreaTemplateJson = """{"areas":[]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        var idxA = text.IndexOf("/ha/entities/light/a_lamp", StringComparison.Ordinal);
        var idxB = text.IndexOf("/ha/entities/light/b_lamp", StringComparison.Ordinal);
        idxA.ShouldBeGreaterThanOrEqualTo(0);
        idxB.ShouldBeGreaterThan(idxA);
    }

    [Fact]
    public async Task GetAsync_EntityWithoutFriendlyName_OmitsSlugSuffix()
    {
        var client = new FakeHaClient
        {
            States = { Entity("switch.bare", "off") },
            AreaTemplateJson = """{"areas":[]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("/ha/entities/switch/bare\n");
        text.ShouldContain("/ha/areas/unassigned/switch.bare\n");
        text.ShouldNotContain("switch.bare_(");
    }

    [Fact]
    public async Task GetAsync_EmptyCatalog_ReturnsEmpty()
    {
        var client = new FakeHaClient();
        (await Build(client).GetAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}