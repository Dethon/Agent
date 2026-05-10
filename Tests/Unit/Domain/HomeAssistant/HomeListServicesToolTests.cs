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
    }
}
