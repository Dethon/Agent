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
    public async Task ListServicesAsync_CapturesTargetWhenPresent()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                domain = "roborock",
                services = new Dictionary<string, object>
                {
                    ["get_maps"] = new
                    {
                        description = "Get maps",
                        fields = new Dictionary<string, object>(),
                        target = new
                        {
                            entity = new[]
                            {
                                new { integration = "roborock", domain = new[] { "vacuum" } }
                            }
                        }
                    }
                }
            },
            new
            {
                domain = "homeassistant",
                services = new Dictionary<string, object>
                {
                    ["restart"] = new
                    {
                        description = "Restart HA",
                        fields = new Dictionary<string, object>()
                        // no target key
                    }
                }
            }
        });
        _server.Given(Request.Create().WithPath("/api/services").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

        var result = await _client.ListServicesAsync();

        var getMaps = result.Single(s => s.Service == "get_maps");
        getMaps.Target.ShouldNotBeNull();
        getMaps.Target!["entity"]![0]!["integration"]!.GetValue<string>().ShouldBe("roborock");
        getMaps.Target["entity"]![0]!["domain"]![0]!.GetValue<string>().ShouldBe("vacuum");

        var restart = result.Single(s => s.Service == "restart");
        restart.Target.ShouldBeNull();
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
    public async Task CallServiceAsync_SendsEntityIdFlatInBody()
    {
        var responseBody = JsonSerializer.Serialize(new[]
        {
            new { entity_id = "vacuum.s8", state = "cleaning", attributes = new Dictionary<string, object>() }
        });
        StubServiceUnsupportedResponse("/api/services/vacuum/start");
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
        result.Response.ShouldBeNull();

        var posted = JsonNode.Parse(_server.LogEntries.Last().RequestMessage?.Body!)!.AsObject();
        posted["entity_id"]!.GetValue<string>().ShouldBe("vacuum.s8");
        posted["mode"]!.GetValue<string>().ShouldBe("spot");
        posted.ContainsKey("target").ShouldBeFalse();
    }

    [Fact]
    public async Task CallServiceAsync_NoEntityId_OmitsEntityIdAndTarget()
    {
        StubServiceUnsupportedResponse("/api/services/homeassistant/restart");
        _server.Given(Request.Create().WithPath("/api/services/homeassistant/restart").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        await _client.CallServiceAsync("homeassistant", "restart", null, null);

        var posted = JsonNode.Parse(_server.LogEntries.Last().RequestMessage?.Body!)!.AsObject();
        posted.ContainsKey("target").ShouldBeFalse();
        posted.ContainsKey("entity_id").ShouldBeFalse();
    }

    [Fact]
    public async Task CallServiceAsync_RequestsResponseByDefault()
    {
        // Service supports response: HA returns {changed_states, service_response} with 200.
        var responseBody = """
            {
              "changed_states": [
                {"entity_id":"script.echo","state":"on","attributes":{}}
              ],
              "service_response": {"echoed":"hello"}
            }
            """;
        _server.Given(Request.Create()
                .WithPath("/api/services/script/echo")
                .WithParam("return_response", "true")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(responseBody)
                .WithHeader("Content-Type", "application/json"));

        var data = new Dictionary<string, JsonNode?> { ["value"] = JsonValue.Create("hello") };
        var result = await _client.CallServiceAsync("script", "echo", null, data);

        result.ChangedEntities.Count.ShouldBe(1);
        result.ChangedEntities[0].EntityId.ShouldBe("script.echo");
        result.Response.ShouldNotBeNull();
        result.Response!["echoed"]!.GetValue<string>().ShouldBe("hello");

        var url = _server.LogEntries.Last().RequestMessage?.AbsoluteUrl;
        url.ShouldContain("return_response=true");
    }

    [Fact]
    public async Task CallServiceAsync_400DoesNotSupportResponses_FallsBackWithoutQuery()
    {
        // First call (with return_response=true) → 400 with the canonical HA message.
        // Second call (no query) → 200 with array shape.
        _server.Given(Request.Create()
                .WithPath("/api/services/light/turn_on")
                .WithParam("return_response", "true")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Service does not support responses. Remove return_response from request."}"""));

        var success = JsonSerializer.Serialize(new[]
        {
            new { entity_id = "light.kitchen", state = "on", attributes = new Dictionary<string, object>() }
        });
        _server.Given(Request.Create()
                .WithPath("/api/services/light/turn_on")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(success));

        var result = await _client.CallServiceAsync("light", "turn_on", "light.kitchen", null);

        result.ChangedEntities.Count.ShouldBe(1);
        result.ChangedEntities[0].EntityId.ShouldBe("light.kitchen");
        result.Response.ShouldBeNull();

        // Two posts: first with the query, second without.
        var posts = _server.LogEntries
            .Where(e => e.RequestMessage?.Method == "POST")
            .ToList();
        posts.Count.ShouldBe(2);
        posts[0].RequestMessage!.AbsoluteUrl.ShouldContain("return_response=true");
        posts[1].RequestMessage!.AbsoluteUrl.ShouldNotContain("return_response");
    }

    [Fact]
    public async Task CallServiceAsync_400OtherReason_PropagatesAndDoesNotRetry()
    {
        _server.Given(Request.Create()
                .WithPath("/api/services/light/turn_on")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Some unrelated validation error"}"""));

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => _client.CallServiceAsync("light", "turn_on", "light.kitchen", null));
        ex.StatusCode.ShouldBe(400);

        // Exactly one POST — no retry.
        _server.LogEntries.Count(e => e.RequestMessage?.Method == "POST").ShouldBe(1);
    }

    private void StubServiceUnsupportedResponse(string path)
        => _server.Given(Request.Create()
                .WithPath(path)
                .WithParam("return_response", "true")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Service does not support responses. Remove return_response from request."}"""));

    [Fact]
    public async Task RenderTemplateAsync_PostsBodyAndReturnsRendered()
    {
        _server.Given(Request.Create()
                .WithPath("/api/template")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("Salón,Cocina,Dormitorio"));

        var result = await _client.RenderTemplateAsync("{{ areas() | map('area_name') | join(',') }}");

        result.ShouldBe("Salón,Cocina,Dormitorio");
        var posted = JsonNode.Parse(_server.LogEntries.Last().RequestMessage?.Body!)!.AsObject();
        posted["template"]!.GetValue<string>().ShouldBe("{{ areas() | map('area_name') | join(',') }}");
    }

    [Fact]
    public async Task ListStatesAsync_401_ThrowsUnauthorized()
    {
        _server.Given(Request.Create().WithPath("/api/states").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("Unauthorized"));

        var ex = await Should.ThrowAsync<HomeAssistantUnauthorizedException>(
            () => _client.ListStatesAsync());
        ex.StatusCode.ShouldBe(401);
    }
}
