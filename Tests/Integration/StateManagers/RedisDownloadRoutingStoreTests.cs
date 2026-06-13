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
    public async Task SetAsync_ThenListAndRemove_ReflectsMutations()
    {
        _createdIds.AddRange([101, 102]);

        await _store.SetAsync(Routing(101));
        await _store.SetAsync(Routing(102));

        var listed = await _store.ListAsync();
        listed.Select(r => r.DownloadId).ShouldBe([101, 102], ignoreOrder: true);
        listed.Single(r => r.DownloadId == 101).Context.Origin.ChannelId.ShouldBe("signalr");

        await _store.RemoveAsync(101);
        (await _store.ListAsync()).Select(r => r.DownloadId).ShouldBe([102]);
    }

    [Fact]
    public async Task SetAsync_SameId_OverwritesExistingEntry()
    {
        _createdIds.Add(201);

        await _store.SetAsync(Routing(201));
        await _store.SetAsync(Routing(201) with { Title = "updated" });

        (await _store.ListAsync()).Single(r => r.DownloadId == 201).Title.ShouldBe("updated");
    }

    [Fact]
    public async Task ListAsync_EntryExpired_SelfHealsIndex()
    {
        _createdIds.Add(301);
        await _store.SetAsync(Routing(301));

        var db = fixture.Connection.GetDatabase();
        await db.KeyDeleteAsync("download-routing:301");

        (await _store.ListAsync()).ShouldNotContain(r => r.DownloadId == 301);
        (await db.SetContainsAsync("download-routing", 301)).ShouldBeFalse(
            "Stale index member should be removed when its entry has expired");
    }

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}")),
        SubmittedAt = DateTimeOffset.UtcNow
    };
}