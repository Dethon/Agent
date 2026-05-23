using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogProviderTests
{
    [Fact]
    public async Task GetAsync_BuildsCatalogFromClient()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.kitchen"]}]}"""
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(1);
        catalog.Services.Count.ShouldBe(1);
        catalog.Areas.ShouldContain(a => a.Id == "salon" && a.EntityIds.Contains("light.kitchen"));
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl()
    {
        var client = new CountingClient { States = { Entity("light.kitchen", "off") } };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        await provider.GetAsync(CancellationToken.None);
        await provider.GetAsync(CancellationToken.None);

        client.StateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_OnFailure_ReturnsEmpty()
    {
        var provider = new HaCatalogProvider(() => new ThrowingClient(), new FakeTimeProvider());
        (await provider.GetAsync(CancellationToken.None)).Entities.ShouldBeEmpty();
    }

    private sealed class CountingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return base.ListStatesAsync(ct);
        }
    }

    private sealed class ThrowingClient : FakeHaClient
    {
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("HA down");
    }
}