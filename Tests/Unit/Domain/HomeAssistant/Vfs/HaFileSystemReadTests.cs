using System.Text.Json.Nodes;
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
    public async Task ReadAsync_StateFile_RendersFreshYaml()
    {
        var fs = Build(out _);
        var read = await fs.ReadAsync("entities/light/kitchen/state.yaml", null, null, CancellationToken.None);
        read["content"]!.GetValue<string>().ShouldContain("entity_id: light.kitchen");
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
    public async Task SearchAsync_FindsEntityByState()
    {
        var fs = Build(out _);
        var result = await fs.SearchAsync("off", false, null, null, null, 50, 1, CancellationToken.None);
        result["totalMatches"]!.GetValue<int>().ShouldBeGreaterThan(0);
        result["results"]!.AsArray().Count.ShouldBeGreaterThan(0);
        result["results"]![0]!["file"]!.GetValue<string>().ShouldContain("light/kitchen");
    }
}