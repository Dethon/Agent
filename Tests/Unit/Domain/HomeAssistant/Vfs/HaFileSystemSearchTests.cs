using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemSearchTests
{
    private const string KitchenStateFile = "entities/light/kitchen_(kitchen)/state.yaml";

    private static HaFileSystem Build()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("light.salon", "on", ("friendly_name", JsonValue.Create("Salon"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp")))
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.salon","sensor.salon_temp"]}]}"""
        };
        return new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
    }

    [Fact]
    public async Task SearchAsync_NoScope_SearchesAllEntities()
    {
        var result = await Build().SearchAsync(
            "state", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["filesSearched"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public async Task SearchAsync_DirectoryPath_ScopesToClass()
    {
        var result = await Build().SearchAsync(
            "state", false, null, "entities/sensor", null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["results"]!.AsArray().Select(r => r!["file"]!.GetValue<string>()).ToList()
            .ShouldAllBe(f => f.Contains("/sensor/"));
    }

    [Fact]
    public async Task SearchAsync_DirectoryPath_ScopesToArea()
    {
        var result = await Build().SearchAsync(
            "state", false, null, "areas/salon", null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["filesSearched"]!.GetValue<int>().ShouldBe(2);
        result["results"]!.AsArray().Select(r => r!["file"]!.GetValue<string>()).ToList()
            .ShouldNotContain(f => f.Contains("kitchen"));
    }

    [Fact]
    public async Task SearchAsync_SingleFilePath_ScopesToOneEntity()
    {
        var result = await Build().SearchAsync(
            "state", false, KitchenStateFile, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["results"]![0]!["file"]!.GetValue<string>().ShouldContain("kitchen");
    }

    [Fact]
    public async Task SearchAsync_FilesOnlyOutputMode_ReturnsMatchCountWithoutMatches()
    {
        var result = await Build().SearchAsync(
            "state", false, null, null, null, 50, 1, VfsTextSearchOutputMode.FilesOnly, CancellationToken.None);
        var first = result["results"]![0]!.AsObject();
        first.ContainsKey("matchCount").ShouldBeTrue();
        first.ContainsKey("matches").ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_ContextLines_IncludesSurroundingLines()
    {
        var result = await Build().SearchAsync(
            "attributes", false, KitchenStateFile, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        var match = result["results"]![0]!["matches"]![0]!;
        match["context"].ShouldNotBeNull();
        match["context"]!["after"]!.AsArray().Select(l => l!.GetValue<string>()).ToList()
            .ShouldContain(l => l.Contains("friendly_name"));
    }

    [Fact]
    public async Task SearchAsync_FilePattern_NonMatching_ReturnsNoResults()
    {
        var result = await Build().SearchAsync(
            "state", false, null, null, "*.sh", 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["filesSearched"]!.GetValue<int>().ShouldBe(0);
        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
        result["results"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_InvalidRegex_ReturnsInvalidArgumentWithHint()
    {
        var result = await Build().SearchAsync(
            "(unclosed", true, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["hint"]!.GetValue<string>().ShouldContain("regex=false");
    }
}