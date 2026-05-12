using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeListEntitiesToolTests
{
    private static HaEntityState Entity(string id, string state, string? friendly = null)
        => new()
        {
            EntityId = id,
            State = state,
            Attributes = friendly is null
                ? new Dictionary<string, JsonNode?>()
                : new Dictionary<string, JsonNode?> { ["friendly_name"] = JsonValue.Create(friendly) }
        };

    [Fact]
    public async Task RunAsync_FiltersByDomain()
    {
        var client = new FakeHaClient(
            Entity("vacuum.s8", "docked", "Roborock"),
            Entity("light.kitchen", "on"),
            Entity("vacuum.spare", "cleaning"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: "vacuum", area: null, limit: 100, ct: CancellationToken.None);

        var entities = (JsonArray)result["entities"]!;
        entities.Count.ShouldBe(2);
        entities.Select(e => (string)e!["entity_id"]!).ShouldBe(["vacuum.s8", "vacuum.spare"]);
    }

    [Fact]
    public async Task RunAsync_FiltersByAreaAgainstFriendlyName()
    {
        var client = new FakeHaClient(
            Entity("light.kitchen", "on", "Kitchen Ceiling"),
            Entity("light.bedroom", "off", "Bedroom Lamp"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: "kitchen", limit: 100, ct: CancellationToken.None);

        var entities = (JsonArray)result["entities"]!;
        entities.Count.ShouldBe(1);
        ((string)entities[0]!["entity_id"]!).ShouldBe("light.kitchen");
    }

    [Fact]
    public async Task RunAsync_AppliesLimit()
    {
        var client = new FakeHaClient(
            Entity("light.a", "on"),
            Entity("light.b", "on"),
            Entity("light.c", "on"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: null, limit: 2, ct: CancellationToken.None);

        ((JsonArray)result["entities"]!).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_ProjectsExpectedFields()
    {
        var client = new FakeHaClient(Entity("light.kitchen", "on", "Kitchen"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: null, limit: 10, ct: CancellationToken.None);

        var item = ((JsonArray)result["entities"]!)[0]!;
        ((string)item["entity_id"]!).ShouldBe("light.kitchen");
        ((string)item["state"]!).ShouldBe("on");
        ((string)item["domain"]!).ShouldBe("light");
        ((string)item["friendly_name"]!).ShouldBe("Kitchen");
    }

    private sealed class TestableHomeListEntitiesTool(IHomeAssistantClient client)
        : HomeListEntitiesTool(client)
    {
        public new Task<JsonObject> RunAsync(string? domain, string? area, int? limit, CancellationToken ct)
            => base.RunAsync(domain, area, limit, ct);
    }

    private sealed class FakeHaClient(params HaEntityState[] entities) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaEntityState>>(entities);

        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();

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
