using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeCallServiceToolTests
{
    [Fact]
    public async Task RunAsync_PassesEntityIdAndDataToClient()
    {
        var client = new RecordingClient(new HaServiceCallResult
        {
            ChangedEntities = new List<HaEntityState>
            {
                new() { EntityId = "vacuum.s8", State = "cleaning" }
            }
        });
        var tool = new TestableHomeCallServiceTool(client);

        var data = new JsonObject { ["mode"] = "spot" };
        var result = await tool.RunAsync("vacuum", "start", "vacuum.s8", data, CancellationToken.None);

        client.LastDomain.ShouldBe("vacuum");
        client.LastService.ShouldBe("start");
        client.LastEntityId.ShouldBe("vacuum.s8");
        client.LastData!["mode"]!.GetValue<string>().ShouldBe("spot");

        ((bool)result["ok"]!).ShouldBeTrue();
        var changed = (JsonArray)result["changed_entities"]!;
        ((string)changed[0]!["entity_id"]!).ShouldBe("vacuum.s8");
        ((string)changed[0]!["state"]!).ShouldBe("cleaning");
    }

    [Fact]
    public async Task RunAsync_NoServiceResponse_OmitsResponseField()
    {
        var client = new RecordingClient(new HaServiceCallResult { ChangedEntities = [] });
        var tool = new TestableHomeCallServiceTool(client);

        var result = await tool.RunAsync("light", "turn_on", "light.kitchen", null, CancellationToken.None);

        result.ContainsKey("response").ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithServiceResponse_SurfacesResponseField()
    {
        var responseNode = JsonNode.Parse("""{"echoed":"hello"}""");
        var client = new RecordingClient(new HaServiceCallResult
        {
            ChangedEntities = [],
            Response = responseNode
        });
        var tool = new TestableHomeCallServiceTool(client);

        var result = await tool.RunAsync("script", "echo", null,
            new JsonObject { ["value"] = "hello" }, CancellationToken.None);

        result.ContainsKey("response").ShouldBeTrue();
        result["response"]!["echoed"]!.GetValue<string>().ShouldBe("hello");
    }

    [Fact]
    public async Task RunAsync_ExplicitEntityIdParameterWinsOverDataEntityId()
    {
        var client = new RecordingClient(new HaServiceCallResult { ChangedEntities = [] });
        var tool = new TestableHomeCallServiceTool(client);

        var data = new JsonObject { ["entity_id"] = "vacuum.wrong" };
        await tool.RunAsync("vacuum", "start", "vacuum.right", data, CancellationToken.None);

        client.LastEntityId.ShouldBe("vacuum.right");
        client.LastData!.ContainsKey("entity_id").ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_AllowsNullEntityIdAndNullData()
    {
        var client = new RecordingClient(new HaServiceCallResult { ChangedEntities = [] });
        var tool = new TestableHomeCallServiceTool(client);

        var result = await tool.RunAsync("homeassistant", "restart", null, null, CancellationToken.None);

        client.LastEntityId.ShouldBeNull();
        client.LastData.ShouldBeNull();
        ((bool)result["ok"]!).ShouldBeTrue();
    }

    private sealed class TestableHomeCallServiceTool(IHomeAssistantClient client) : HomeCallServiceTool(client)
    {
        public new Task<JsonObject> RunAsync(
            string domain, string service, string? entityId, JsonObject? data, CancellationToken ct)
            => base.RunAsync(domain, service, entityId, data, ct);
    }

    private sealed class RecordingClient(HaServiceCallResult result) : IHomeAssistantClient
    {
        public string? LastDomain { get; private set; }
        public string? LastService { get; private set; }
        public string? LastEntityId { get; private set; }
        public IReadOnlyDictionary<string, JsonNode?>? LastData { get; private set; }

        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        {
            LastDomain = domain;
            LastService = service;
            LastEntityId = entityId;
            LastData = data;
            return Task.FromResult(result);
        }
    }
}
