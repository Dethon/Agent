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
    public async Task GetAsync_RendersMountAreasDomainsAndCounts()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off"), Entity("sensor.salon_temp", "21") },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["sensor.salon_temp"]}]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("/ha");
        text.ShouldContain("Salón");
        text.ShouldContain("light");
        text.ShouldContain("sensor");
        text.ShouldContain("2 entities");
    }

    [Fact]
    public async Task GetAsync_EmptyCatalog_ReturnsEmpty()
    {
        var client = new FakeHaClient();
        // No states -> provider treats as failure/empty -> summary empty.
        (await Build(client).GetAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}