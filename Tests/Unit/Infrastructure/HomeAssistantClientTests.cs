using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task GetStateAsync_EntityFound_ReturnsState()
    {
        var body = JsonSerializer.Serialize(new
        {
            entity_id = "light.kitchen",
            state = "on",
            attributes = new Dictionary<string, object> { ["brightness"] = 200 }
        });
        _server.Given(Request.Create().WithPath("/api/states/light.kitchen").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

        var result = await _client.GetStateAsync("light.kitchen");

        result.ShouldNotBeNull();
        result!.EntityId.ShouldBe("light.kitchen");
        result.State.ShouldBe("on");
    }

    [Fact]
    public async Task GetStateAsync_EntityMissing_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/states/light.missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("Entity not found"));

        var result = await _client.GetStateAsync("light.missing");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListServicesAsync_FlattensNestedDomainShape()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                domain = "vacuum",
                services = new Dictionary<string, object>
                {
                    ["start"] = new
                    {
                        description = "Start cleaning",
                        fields = new Dictionary<string, object>
                        {
                            ["entity_id"] = new
                            {
                                description = "Target",
                                required = true,
                                example = "vacuum.s8"
                            }
                        }
                    },
                    ["return_to_base"] = new { description = "Send home", fields = new Dictionary<string, object>() }
                }
            }
        });
        _server.Given(Request.Create().WithPath("/api/services").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

        var result = await _client.ListServicesAsync();

        result.Count.ShouldBe(2);
        var start = result.Single(s => s.Service == "start");
        start.Domain.ShouldBe("vacuum");
        start.Description.ShouldBe("Start cleaning");
        start.Fields["entity_id"].Required.ShouldBeTrue();
        start.Fields["entity_id"].Description.ShouldBe("Target");
        start.Fields["entity_id"].Example!.GetValue<string>().ShouldBe("vacuum.s8");
    }

    [Fact]
    public async Task CallServiceAsync_HoistsEntityIdIntoTargetAndSendsBody()
    {
        var responseBody = JsonSerializer.Serialize(new[]
        {
            new { entity_id = "vacuum.s8", state = "cleaning", attributes = new Dictionary<string, object>() }
        });
        _server.Given(Request.Create()
                .WithPath("/api/services/vacuum/start")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(responseBody));

        var data = new Dictionary<string, JsonNode?> { ["mode"] = JsonValue.Create("spot") };
        var result = await _client.CallServiceAsync("vacuum", "start", "vacuum.s8", data);

        result.ChangedEntities.Count.ShouldBe(1);
        result.ChangedEntities[0].EntityId.ShouldBe("vacuum.s8");
        result.ChangedEntities[0].State.ShouldBe("cleaning");

        var calls = _server.LogEntries.ToList();
        var posted = JsonNode.Parse(calls.Last().RequestMessage.Body!)!.AsObject();
        posted["target"]!["entity_id"]!.GetValue<string>().ShouldBe("vacuum.s8");
        posted["mode"]!.GetValue<string>().ShouldBe("spot");
    }

    [Fact]
    public async Task CallServiceAsync_NoEntityId_OmitsTarget()
    {
        _server.Given(Request.Create().WithPath("/api/services/homeassistant/restart").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        await _client.CallServiceAsync("homeassistant", "restart", null, null);

        var posted = JsonNode.Parse(_server.LogEntries.Last().RequestMessage.Body!)!.AsObject();
        posted.ContainsKey("target").ShouldBeFalse();
    }
}
