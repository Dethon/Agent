using Observability.Services;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Observability;

public sealed class ServiceHealthRegistryTests(RedisFixture fixture) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

    private IDatabase Db => fixture.Connection.GetDatabase();

    public Task InitializeAsync() => Db.KeyDeleteAsync(ServiceHealthRegistry.SeenKey);

    public Task DisposeAsync() => Db.KeyDeleteAsync(ServiceHealthRegistry.SeenKey);

    [Fact]
    public async Task ListAsync_KeepsRecentlySeenServicesAndDropsTheRest()
    {
        await ServiceHealthRegistry.MarkSeenAsync(Db, "lemonade", _now);
        await ServiceHealthRegistry.MarkSeenAsync(Db, "tse-extractor", _now - TimeSpan.FromDays(6));
        await ServiceHealthRegistry.MarkSeenAsync(Db, "wyoming-piper", _now - TimeSpan.FromDays(30));

        var roster = await ServiceHealthRegistry.ListAsync(Db, _now);

        roster.ShouldBe(["lemonade", "tse-extractor"], ignoreOrder: true);
        (await Db.SortedSetLengthAsync(ServiceHealthRegistry.SeenKey)).ShouldBe(2);
    }

    [Fact]
    public async Task MarkSeenAsync_SameServiceTwice_RefreshesTheScoreInsteadOfDuplicating()
    {
        await ServiceHealthRegistry.MarkSeenAsync(Db, "lemonade", _now - TimeSpan.FromDays(30));
        await ServiceHealthRegistry.MarkSeenAsync(Db, "lemonade", _now);

        (await ServiceHealthRegistry.ListAsync(Db, _now)).ShouldBe(["lemonade"]);
        (await Db.SortedSetScoreAsync(ServiceHealthRegistry.SeenKey, "lemonade"))
            .ShouldBe(_now.ToUnixTimeSeconds());
    }
}