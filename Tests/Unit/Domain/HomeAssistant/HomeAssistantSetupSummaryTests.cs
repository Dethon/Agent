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

    [Fact]
    public async Task GetAsync_ListsAvailableActionsPerEntityClass()
    {
        // The prompt used to tell the agent to `glob <entity-dir>/*.sh` to discover actions, which
        // cost a round trip per turn — and its first guess, the CLASS directory, always returns
        // nothing because action files live one level deeper. Naming the actions up front removes
        // both turns for ~350 tokens, a trade worth roughly 100:1 against a ~1.15s round trip.
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("light.desk", "on", ("friendly_name", JsonValue.Create("Desk"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp"))),
            },
            Services =
            {
                Service("light", "turn_on", DomainTarget("light")),
                Service("light", "turn_off", DomainTarget("light")),
            }
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("## Actions by entity class");
        text.ShouldContain("light: turn_off.sh, turn_on.sh");
        // A read-only class must not appear at all rather than as an empty entry.
        text.ShouldNotContain("sensor:");
    }

    [Fact]
    public async Task GetAsync_ActionTableSaysWhereTheFilesLive()
    {
        // The wasted glob is structural: `*.sh` at the class level legitimately returns 0.
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", DomainTarget("light")) }
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("entity directory");
    }

    [Fact]
    public async Task GetAsync_WithNoActionableEntities_OmitsTheActionTable()
    {
        var client = new FakeHaClient { States = { Entity("sensor.salon_temp", "21") } };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldNotContain("## Actions by entity class");
    }

}