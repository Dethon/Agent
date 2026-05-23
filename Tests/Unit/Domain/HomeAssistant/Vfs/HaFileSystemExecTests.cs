using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemExecTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) }
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task Exec_CallsService_WithParsedData()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall!.Value.Domain.ShouldBe("light");
        client.LastCall.Value.Service.ShouldBe("turn_on");
        client.LastCall.Value.EntityId.ShouldBe("light.kitchen");
        client.LastCall.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);
    }

    [Fact]
    public async Task Exec_Help_ReturnsUsage_ExitZero_NoCall()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --help", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["stdout"]!.GetValue<string>().ShouldContain("--brightness_pct");
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_DotSlashPrefixAccepted()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "./turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        client.LastCall.ShouldNotBeNull();
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127_WithAvailableActions()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "cat state.json", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(127);
        result["stderr"]!.GetValue<string>().ShouldContain("turn_on.sh");
    }

    [Fact]
    public async Task Exec_BadArg_Returns2()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --nope 1", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(2);
        result["stderr"]!.GetValue<string>().ShouldContain("nope");
    }

    [Fact]
    public async Task Exec_BadScalarValue_Returns2()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct NaN", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Exec_NotAnEntityDir_Returns127()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light", "turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(127);
    }

    [Fact]
    public async Task Exec_HaFailure_Returns1_WithHint()
    {
        var fs = Build(out var client);
        client.CallHandler = (_, _, _, _) => throw new HomeAssistantException("400 bad field", 400);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(1);
        result["stderr"]!.GetValue<string>().ShouldContain("400 bad field");
        result["stderr"]!.GetValue<string>().ShouldContain("--help");
    }

    [Fact]
    public async Task ExecAsync_Success_ReportsCwdAndDuration()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
        result["cwd"]!.GetValue<string>().ShouldBe("entities/light/kitchen_(kitchen)");
        result["durationMs"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(0);
        FsResultContract.TryValidate("fs_exec", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task ExecAsync_HonorsTimeout_ReturnsTimedOut()
    {
        var client = new BlockingHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh", 1, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
        result["exitCode"]!.GetValue<int>().ShouldBe(-1);
        FsResultContract.TryValidate("fs_exec", result, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecAsync_UnknownEntity_127_NoHint()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/ghost", "turn_on.sh", null, CancellationToken.None);
        result["exitCode"]!.GetValue<int>().ShouldBe(127);
        result["stderr"]!.GetValue<string>().ShouldNotContain("Did you mean");
    }

    private sealed class BlockingHaClient : FakeHaClient
    {
        public override async Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HaServiceCallResult { ChangedEntities = [] };
        }
    }
}