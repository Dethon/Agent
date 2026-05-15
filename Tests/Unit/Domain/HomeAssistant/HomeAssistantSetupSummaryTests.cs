using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Prompts;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeAssistantSetupSummaryTests
{
    [Fact]
    public async Task BuildAsync_FlagsIntegrationServiceDomains_NotClassDomains()
    {
        var fake = new FakeClient(
            services:
            [
                Svc("vacuum", "start"),
                Svc("light", "turn_on"),
                Svc("roborock", "get_maps"),
                Svc("hue", "activate_scene"),
                Svc("homeassistant", "restart")
            ]);
        var summary = new HomeAssistantSetupSummary(() => fake);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        rendered.ShouldContain("### Integration service domains");
        // Vendor/integration domains surface verbatim.
        rendered.ShouldContain("hue");
        rendered.ShouldContain("roborock");
        // Class domains are filtered out of the integration list (they remain visible
        // only via the "entities by class domain" section).
        rendered.ShouldNotContain(", vacuum,");
        rendered.ShouldNotContain(", light,");
        rendered.ShouldNotContain(", homeassistant,");
    }

    [Fact]
    public async Task BuildAsync_GroupsAreasWithEntities_AndUnassigned()
    {
        var fake = new FakeClient(
            states:
            [
                Entity("light.salon_lamp", "Lámpara Salón"),
                Entity("light.cocina_techo", "Techo Cocina"),
                Entity("sensor.salon_temperature", "Temperatura Salón"),
                Entity("vacuum.roborock_s8", "Roborock S8")
            ],
            areasJson: """
                {"areas":[
                    {"id":"salon_id","name":"Salón","entities":["light.salon_lamp","sensor.salon_temperature"]},
                    {"id":"cocina_id","name":"Cocina","entities":["light.cocina_techo"]}
                ]}
                """);
        var summary = new HomeAssistantSetupSummary(() => fake);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        rendered.ShouldContain("**Salón**");
        // Friendly names sit next to the entity_id so the agent can match "the lamp in the salón".
        rendered.ShouldContain("light.salon_lamp (Lámpara Salón)");
        rendered.ShouldContain("sensor.salon_temperature (Temperatura Salón)");
        rendered.ShouldContain("**Cocina**");
        rendered.ShouldContain("light.cocina_techo (Techo Cocina)");
        // vacuum.roborock_s8 isn't in any area → flagged as unassigned with a count.
        rendered.ShouldContain("**(unassigned)** — 1 entities");
    }

    [Fact]
    public async Task BuildAsync_EntityWithoutFriendlyName_FallsBackToEntityId()
    {
        var fake = new FakeClient(
            states: [Entity("light.kitchen")], // no friendly_name attribute
            areasJson: """{"areas":[{"id":"k","name":"Kitchen","entities":["light.kitchen"]}]}""");
        var summary = new HomeAssistantSetupSummary(() => fake);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        // No "(...)" suffix when friendly_name equals entity_id (i.e. is missing).
        rendered.ShouldNotContain("light.kitchen (light.kitchen)");
        rendered.ShouldContain("light.kitchen");
    }

    [Fact]
    public async Task BuildAsync_NoAreas_RendersGracefulFallback()
    {
        var fake = new FakeClient(
            states: [Entity("light.kitchen")],
            areasJson: """{"areas":[]}""");
        var summary = new HomeAssistantSetupSummary(() => fake);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        rendered.ShouldContain("No areas configured in HA");
    }

    [Fact]
    public async Task BuildAsync_GroupsEntitiesByClassDomain_WithCounts()
    {
        var fake = new FakeClient(
            states:
            [
                Entity("light.a", "Lampara A"), Entity("light.b", "Lampara B"), Entity("light.c"),
                Entity("vacuum.s8", "Roborock"),
                Entity("sensor.temperature", "Termo"), Entity("sensor.humidity", "Humedad")
            ]);
        var summary = new HomeAssistantSetupSummary(() => fake);

        var rendered = await summary.BuildAsync(CancellationToken.None);

        rendered.ShouldContain("### Entities by class domain");
        // Each entity shows its friendly_name in parens; light.c has none → bare entity_id.
        rendered.ShouldContain("**light** (3): light.a (Lampara A), light.b (Lampara B), light.c");
        rendered.ShouldContain("**sensor** (2): sensor.humidity (Humedad), sensor.temperature (Termo)");
        rendered.ShouldContain("**vacuum** (1): vacuum.s8 (Roborock)");
    }

    [Fact]
    public async Task GetAsync_CachesResultBetweenCalls()
    {
        var fake = new FakeClient(states: [Entity("light.kitchen")]);
        var summary = new HomeAssistantSetupSummary(() => fake);

        await summary.GetAsync();
        await summary.GetAsync();

        fake.ListStatesCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_OnError_ReturnsEmptyString()
    {
        var fake = new FakeClient(throwOnStates: true);
        var summary = new HomeAssistantSetupSummary(() => fake);

        var result = await summary.GetAsync();

        result.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetAsync_AfterFailure_RebuildsOnceFailureTtlElapses()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fake = new FakeClient(states: [Entity("light.kitchen")]) { ThrowOnStates = true };
        var summary = new HomeAssistantSetupSummary(() => fake, time);

        (await summary.GetAsync()).ShouldBe(string.Empty);
        fake.ListStatesCalls.ShouldBe(1);

        // Within the 30-s failure TTL — still cached, no second call.
        time.Advance(TimeSpan.FromSeconds(15));
        (await summary.GetAsync()).ShouldBe(string.Empty);
        fake.ListStatesCalls.ShouldBe(1);

        // Past the failure TTL with HA recovered — a fresh build runs and the
        // result is now the populated snapshot, not the cached empty.
        time.Advance(TimeSpan.FromSeconds(30));
        fake.ThrowOnStates = false;
        (await summary.GetAsync()).ShouldContain("light.kitchen");
        fake.ListStatesCalls.ShouldBe(2);

        // The successful result holds for the full 30-min success TTL.
        time.Advance(TimeSpan.FromMinutes(20));
        (await summary.GetAsync()).ShouldContain("light.kitchen");
        fake.ListStatesCalls.ShouldBe(2);
    }

    private static HaEntityState Entity(string id, string? friendlyName = null)
    {
        var attributes = new Dictionary<string, JsonNode?>();
        if (friendlyName is not null)
        {
            attributes["friendly_name"] = JsonValue.Create(friendlyName);
        }
        return new HaEntityState { EntityId = id, State = "on", Attributes = attributes };
    }

    private static HaServiceDefinition Svc(string domain, string service)
        => new() { Domain = domain, Service = service };

    private sealed class FakeClient(
        HaEntityState[]? states = null,
        HaServiceDefinition[]? services = null,
        string? areasJson = null,
        bool throwOnStates = false) : IHomeAssistantClient
    {
        public int ListStatesCalls { get; private set; }
        public bool ThrowOnStates { get; set; } = throwOnStates;

        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            ListStatesCalls++;
            if (ThrowOnStates)
            {
                throw new InvalidOperationException("HA unreachable");
            }
            return Task.FromResult<IReadOnlyList<HaEntityState>>(states ?? []);
        }

        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(services ?? []);

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
            => Task.FromResult(areasJson ?? """{"areas":[]}""");
    }
}