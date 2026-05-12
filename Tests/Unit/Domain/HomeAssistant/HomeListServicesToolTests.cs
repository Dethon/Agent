using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeListServicesToolTests
{
    private static HaServiceDefinition Svc(string domain, string service, string? desc = null)
        => new() { Domain = domain, Service = service, Description = desc };

    [Fact]
    public async Task RunAsync_ReturnsAllServicesWhenNoFilter()
    {
        var client = new ServicesClient(
            Svc("vacuum", "start", "Start cleaning"),
            Svc("vacuum", "return_to_base"),
            Svc("light", "turn_on"));
        var tool = new TestableHomeListServicesTool(client);

        var result = await tool.RunAsync(null, CancellationToken.None);

        var arr = (JsonArray)result["services"]!;
        arr.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_FiltersByDomain()
    {
        var client = new ServicesClient(
            Svc("vacuum", "start"),
            Svc("light", "turn_on"));
        var tool = new TestableHomeListServicesTool(client);

        var result = await tool.RunAsync("vacuum", CancellationToken.None);

        var arr = (JsonArray)result["services"]!;
        arr.Count.ShouldBe(1);
        ((string)arr[0]!["domain"]!).ShouldBe("vacuum");
        ((string)arr[0]!["service"]!).ShouldBe("start");
    }

    [Fact]
    public async Task RunAsync_SurfacesTargetWhenPresentAndOmitsWhenAbsent()
    {
        var entityTargeted = new HaServiceDefinition
        {
            Domain = "roborock",
            Service = "get_maps",
            Target = JsonNode.Parse("""{"entity":[{"integration":"roborock","domain":["vacuum"]}]}""")
        };
        var fireAndForget = new HaServiceDefinition
        {
            Domain = "homeassistant",
            Service = "restart"
        };
        var client = new ServicesClient(entityTargeted, fireAndForget);
        var tool = new TestableHomeListServicesTool(client);

        var result = await tool.RunAsync(null, CancellationToken.None);

        var arr = (JsonArray)result["services"]!;
        var getMaps = arr.Single(s => (string)s!["service"]! == "get_maps")!;
        getMaps.AsObject().ContainsKey("target").ShouldBeTrue();
        getMaps["target"]!["entity"]![0]!["domain"]![0]!.GetValue<string>().ShouldBe("vacuum");

        var restart = arr.Single(s => (string)s!["service"]! == "restart")!;
        restart.AsObject().ContainsKey("target").ShouldBeFalse();
    }

    private sealed class TestableHomeListServicesTool(IHomeAssistantClient client) : HomeListServicesTool(client)
    {
        public new Task<JsonObject> RunAsync(string? domain, CancellationToken ct) => base.RunAsync(domain, ct);
    }

    private sealed class ServicesClient(params HaServiceDefinition[] services) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(services);
        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
