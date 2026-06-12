using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public sealed class RedisDownloadRoutingStoreTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisDownloadRoutingStore _store = new(fixture.Connection);
    private readonly List<int> _createdIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _createdIds)
        {
            await _store.RemoveAsync(id);
        }
    }

    [Fact]
    public async Task SetListRemove_RoundTrips()
    {
        _createdIds.AddRange([101, 102]);
        var store = new RedisDownloadRoutingStore(fixture.Connection);

        await store.SetAsync(Routing(101));
        await store.SetAsync(Routing(102));

        var listed = await store.ListAsync();
        listed.Select(r => r.DownloadId).ShouldBe([101, 102], ignoreOrder: true);
        listed.Single(r => r.DownloadId == 101).Context.Origin.ChannelId.ShouldBe("signalr");

        await store.RemoveAsync(101);
        (await store.ListAsync()).Select(r => r.DownloadId).ShouldBe([102]);
    }

    [Fact]
    public async Task Set_SameId_Overwrites()
    {
        _createdIds.Add(201);
        var store = new RedisDownloadRoutingStore(fixture.Connection);

        await store.SetAsync(Routing(201));
        await store.SetAsync(Routing(201) with { Title = "updated" });

        (await store.ListAsync()).Single(r => r.DownloadId == 201).Title.ShouldBe("updated");
    }

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}")),
        SubmittedAt = DateTimeOffset.UtcNow
    };
}