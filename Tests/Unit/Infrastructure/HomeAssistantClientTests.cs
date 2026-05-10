using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

public class HomeAssistantClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListStatesAsync_SendsBearerAndReturnsEntities()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { entity_id = "vacuum.s8", state = "docked",
                  attributes = new Dictionary<string, object> { ["friendly_name"] = "Roborock" },
                  last_changed = "2026-05-10T12:00:00.000000+00:00",
                  last_updated = "2026-05-10T12:01:00.000000+00:00" }
        });
        _server.Given(Request.Create()
                .WithPath("/api/states")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body)
                .WithHeader("Content-Type", "application/json"));

        var result = await _client.ListStatesAsync();

        result.Count.ShouldBe(1);
        result[0].EntityId.ShouldBe("vacuum.s8");
        result[0].State.ShouldBe("docked");
        result[0].Attributes["friendly_name"]!.GetValue<string>().ShouldBe("Roborock");
    }
}
