using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemJourneyTests
{
    [Fact]
    public async Task Discover_Inspect_Help_Act()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) },
            AreaTemplateJson = """{"areas":[{"id":"kitchen","name":"Kitchen","entities":["light.kitchen"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        // 1. discover
        var globResult = await fs.GlobAsync("entities", "*/", CancellationToken.None);
        globResult["entries"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("entities/light/");
        FsResultContract.TryValidate("fs_glob", globResult, out var err).ShouldBeTrue(err);

        // 2. inspect state (the exact directory name a listing returns)
        var state = await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        state["content"]!.GetValue<string>().ShouldContain("\"state\": \"off\"");

        // 3. learn the action
        var help = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh --help", null, CancellationToken.None);
        help["stdout"]!.GetValue<string>().ShouldContain("--brightness_pct");

        // 4. act
        var act = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);
        act["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);

        // 4b. a bare id (when a friendly name exists) is rejected with a hint
        var nearMiss = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);
        nearMiss["exitCode"]!.GetValue<int>().ShouldBe(127);
        nearMiss["stderr"]!.GetValue<string>().ShouldContain("kitchen_(kitchen)");

        // 5. area view resolves to the same entity via its canonical name
        var areaState = await fs.ReadAsync("areas/kitchen/light.kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        areaState["content"]!.GetValue<string>().ShouldContain("\"entity_id\": \"light.kitchen\"");
    }
}