using System.Text.Json.Nodes;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

public class HomeAssistantClientTests(HomeAssistantFixture fixture, ITestOutputHelper output) : IClassFixture<HomeAssistantFixture>
{
    [Fact]
    public async Task ListStatesAsync_ReturnsSeededTestEntity()
    {
        var client = fixture.CreateClient();

        var states = await client.ListStatesAsync();

        states.ShouldNotBeEmpty();
        states.Select(e => e.EntityId).ShouldContain(HomeAssistantFixture.TestEntityId);
    }

    [Fact]
    public async Task GetStateAsync_KnownEntity_ReturnsState()
    {
        var client = fixture.CreateClient();

        var state = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);

        state.ShouldNotBeNull();
        state!.EntityId.ShouldBe(HomeAssistantFixture.TestEntityId);
        state.State.ShouldBeOneOf("on", "off");
    }

    [Fact]
    public async Task GetStateAsync_UnknownEntity_ReturnsNull()
    {
        var client = fixture.CreateClient();

        var state = await client.GetStateAsync("input_boolean.does_not_exist");

        state.ShouldBeNull();
    }

    [Fact]
    public async Task ListServicesAsync_ContainsCoreInputBooleanServices()
    {
        var client = fixture.CreateClient();

        var services = await client.ListServicesAsync();

        var domains = services.Select(s => s.Domain).Distinct().OrderBy(d => d).ToList();
        output.WriteLine($"Service domains ({domains.Count}): {string.Join(", ", domains)}");
        foreach (var s in services.Where(x => x.Domain is "input_boolean" or "homeassistant"))
        {
            output.WriteLine($"  {s.Domain}.{s.Service}");
        }

        services.ShouldContain(s => s.Domain == "input_boolean" && s.Service == "toggle");
        services.ShouldContain(s => s.Domain == "input_boolean" && s.Service == "turn_on");
        services.ShouldContain(s => s.Domain == "homeassistant" && s.Service == "update_entity");
    }

    [Fact]
    public async Task ListServicesAsync_PopulatesTargetForEntityTargetedService()
    {
        var client = fixture.CreateClient();

        var services = await client.ListServicesAsync();

        // input_boolean.toggle is entity-targeted — HA exposes `target.entity[*].domain` so
        // the agent can tell an `entity_id` is required.
        var toggle = services.Single(s => s.Domain == "input_boolean" && s.Service == "toggle");
        toggle.Target.ShouldNotBeNull();
        toggle.Target!["entity"].ShouldNotBeNull();

        // homeassistant.restart is fire-and-forget — no target block in the HA response.
        var restart = services.SingleOrDefault(s => s.Domain == "homeassistant" && s.Service == "restart");
        if (restart is not null)
        {
            restart.Target.ShouldBeNull();
        }
    }

    [Fact]
    public async Task CallServiceAsync_TogglesEntityAndReturnsChangedEntity()
    {
        var client = fixture.CreateClient();
        var initial = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);
        initial.ShouldNotBeNull();
        var before = initial!.State;

        var result = await client.CallServiceAsync(
            domain: "input_boolean",
            service: "toggle",
            entityId: HomeAssistantFixture.TestEntityId,
            data: null);

        result.ChangedEntities.ShouldContain(e => e.EntityId == HomeAssistantFixture.TestEntityId);

        var after = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);
        after.ShouldNotBeNull();
        after!.State.ShouldNotBe(before);
    }

    [Fact]
    public async Task CallServiceAsync_WithDataField_PassesThrough()
    {
        var client = fixture.CreateClient();

        // homeassistant.update_entity accepts an entity_id and triggers a refresh — succeeds
        // for any valid entity. Lets us exercise the data/target round-trip without depending
        // on the test entity's state at the time of the call.
        var data = new Dictionary<string, JsonNode?>
        {
            ["entity_id"] = JsonValue.Create(HomeAssistantFixture.TestEntityId)
        };

        var result = await client.CallServiceAsync(
            domain: "homeassistant",
            service: "update_entity",
            entityId: null,
            data: data);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task CallServiceAsync_ResponseSupportingService_ReturnsServiceResponse()
    {
        var client = fixture.CreateClient();

        var data = new Dictionary<string, JsonNode?>
        {
            ["value"] = JsonValue.Create("hello-from-test")
        };

        var result = await client.CallServiceAsync(
            domain: "script",
            service: "echo",
            entityId: null,
            data: data);

        result.Response.ShouldNotBeNull();
        result.Response!["echoed"]!.GetValue<string>().ShouldBe("hello-from-test");
    }

    [Fact]
    public async Task RenderTemplateAsync_ReturnsRenderedString()
    {
        var client = fixture.CreateClient();

        // {{ now() }} is a stable HA built-in that returns a non-empty rendered value
        // regardless of the user's HA config.
        var rendered = await client.RenderTemplateAsync("{{ now() }}");

        rendered.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SetupSummary_BuildsAgainstRealHA_IncludesExpectedSections()
    {
        var client = fixture.CreateClient();
        var summary = new global::Domain.Prompts.HomeAssistantSetupSummary(() => client);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        output.WriteLine(rendered);

        rendered.ShouldContain("## Current Home Assistant setup");
        rendered.ShouldContain("### Integration service domains");
        rendered.ShouldContain("### Areas");
        rendered.ShouldContain("### Entities by class domain");
        // Both seeded entities — the input_boolean and the script — must appear in the
        // class-domain section.
        rendered.ShouldContain(HomeAssistantFixture.TestEntityId);
        rendered.ShouldContain("script.echo");
    }

    [Fact]
    public async Task ListStatesAsync_BadToken_ThrowsUnauthorized()
    {
        var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl + "/") };
        var bad = new HomeAssistantClient(http, "definitely-not-a-valid-token");

        await Should.ThrowAsync<HomeAssistantUnauthorizedException>(() => bad.ListStatesAsync());
    }
}
