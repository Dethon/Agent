using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemReadTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[]}"""
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task GlobAsync_Directories_ListsEntities()
    {
        var fs = Build(out _);
        var result = (JsonArray)await fs.GlobAsync("entities/light", "*", GlobMode.Directories, CancellationToken.None);
        result.Select(n => n!.GetValue<string>()).ShouldContain("entities/light/kitchen_(kitchen)");
    }

    [Fact]
    public async Task InfoAsync_EntityDir_Exists()
    {
        var fs = Build(out _);
        var info = await fs.InfoAsync("entities/light/kitchen", CancellationToken.None);
        info["exists"]!.GetValue<bool>().ShouldBeTrue();
        info["isDirectory"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task InfoAsync_MissingEntity_ExistsFalse()
    {
        var fs = Build(out _);
        (await fs.InfoAsync("entities/light/ghost", CancellationToken.None))["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_StateFile_RendersFreshJson()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/state.json", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("\"entity_id\": \"light.kitchen\"");
        read["content"]!.GetValue<string>().ShouldContain("1: ");
    }

    [Fact]
    public async Task ReadAsync_ActionFile_RendersHelp()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/turn_on.sh", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("call light.turn_on on light.kitchen");
    }

    [Fact]
    public async Task InfoAsync_ActionFileForMissingEntity_ExistsFalse()
    {
        var fs = Build(out _);
        var info = await fs.InfoAsync("entities/light/ghost/turn_on.sh", CancellationToken.None);
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_ActionFileForMissingEntity_ReturnsNotFound()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/ghost/turn_on.sh", null, null, CancellationToken.None);
        read["ok"]!.GetValue<bool>().ShouldBeFalse();
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
    }

    [Fact]
    public async Task SearchAsync_FindsEntityByState()
    {
        var fs = Build(out _);
        var result = await fs.SearchAsync(
            "off", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["totalMatches"]!.GetValue<int>().ShouldBeGreaterThan(0);
        result["results"]!.AsArray().Count.ShouldBeGreaterThan(0);
        result["results"]![0]!["file"]!.GetValue<string>().ShouldContain("light/kitchen_(kitchen)");
    }

    [Fact]
    public async Task GlobAsync_TwoSameClassEntities_AreDistinguishableByName()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))),
                Entity("climate.0x02", "heat", ("friendly_name", JsonValue.Create("Calefacción Salón")))
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01","climate.0x02"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var hits = ((JsonArray)await fs.GlobAsync("areas/salon", "*", GlobMode.Directories, CancellationToken.None))
            .Select(n => n!.GetValue<string>()).ToList();

        hits.ShouldContain("areas/salon/climate.0x01_(aire-acondicionado-salon)");
        hits.ShouldContain("areas/salon/climate.0x02_(calefaccion-salon)");
    }

    [Fact]
    public async Task ExecAsync_ResolvesViaCompositePath()
    {
        var client = new FakeHaClient
        {
            States = { Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))) },
            Services = { Service("climate", "turn_off", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync(
            "areas/salon/climate.0x01_(aire-acondicionado-salon)", "turn_off.sh", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.EntityId.ShouldBe("climate.0x01");
    }
}