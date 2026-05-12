using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeGetStateToolTests
{
    [Fact]
    public async Task RunAsync_EntityFound_ReturnsFullState()
    {
        var entity = new HaEntityState
        {
            EntityId = "vacuum.s8",
            State = "docked",
            Attributes = new Dictionary<string, JsonNode?>
            {
                ["friendly_name"] = JsonValue.Create("Roborock"),
                ["battery_level"] = JsonValue.Create(95)
            },
            LastChanged = DateTimeOffset.Parse("2026-05-10T12:00:00Z"),
            LastUpdated = DateTimeOffset.Parse("2026-05-10T12:01:00Z")
        };
        var client = new SingleEntityClient(entity);
        var tool = new TestableHomeGetStateTool(client);

        var result = await tool.RunAsync("vacuum.s8", CancellationToken.None);

        ((bool)result["ok"]!).ShouldBeTrue();
        ((string)result["state"]!).ShouldBe("docked");
        ((int)result["attributes"]!["battery_level"]!).ShouldBe(95);
        ((string)result["last_changed"]!).ShouldBe("2026-05-10T12:00:00.0000000+00:00");
    }

    [Fact]
    public async Task RunAsync_EntityNotFound_ReturnsNotFoundEnvelope()
    {
        var client = new SingleEntityClient(null);
        var tool = new TestableHomeGetStateTool(client);

        var result = await tool.RunAsync("vacuum.missing", CancellationToken.None);

        ((bool)result["ok"]!).ShouldBeFalse();
        ((string)result["errorCode"]!).ShouldBe("not_found");
        ((string)result["message"]!).ShouldContain("vacuum.missing");
    }

    private sealed class TestableHomeGetStateTool(IHomeAssistantClient client) : HomeGetStateTool(client)
    {
        public new Task<JsonObject> RunAsync(string entityId, CancellationToken ct)
            => base.RunAsync(entityId, ct);
    }

    private sealed class SingleEntityClient(HaEntityState? entity) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => Task.FromResult(entity);

        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
