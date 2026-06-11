using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

[Trait("Category", "Integration")]
public class RedisThreadStateStoreTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    private RedisThreadStateStore NewStore() =>
        new(redisFixture.Connection, TimeSpan.FromMinutes(5));

    [Fact]
    public async Task AppendMessagesAsync_ToFreshKey_StoresMessagesInOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "hi there")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["hello", "hi there"]);
        messages.Select(m => m.Role).ShouldBe([ChatRole.User, ChatRole.Assistant]);
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingList_PreservesOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "first")]);
        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.Assistant, "second"),
            new ChatMessage(ChatRole.User, "third")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task SetMessagesAsync_ReplacesExistingHistory()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "will be replaced")]);
        await store.SetMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "replacement a"),
            new ChatMessage(ChatRole.Assistant, "replacement b")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["replacement a", "replacement b"]);
    }

    [Fact]
    public async Task AppendMessagesAsync_SetsExpiration()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "ttl check")]);

        var ttl = await redisFixture.Connection.GetDatabase().KeyTimeToLiveAsync(key);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetTailMessagesAsync_ListLongerThanMax_ReturnsOnlyTailInOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key,
            [.. Enumerable.Range(0, 10).Select(i => new ChatMessage(ChatRole.User, $"m{i}"))]);

        var tail = await store.GetTailMessagesAsync(key, 3);

        tail.ShouldNotBeNull();
        tail.Select(m => m.Text).ShouldBe(["m7", "m8", "m9"]);
    }

    [Fact]
    public async Task GetTailMessagesAsync_MaxLargerThanList_ReturnsAllMessages()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "only")]);

        var tail = await store.GetTailMessagesAsync(key, 50);

        tail.ShouldNotBeNull();
        tail.ShouldHaveSingleItem().Text.ShouldBe("only");
    }

    [Fact]
    public async Task GetMessageCountAsync_ReturnsListLength_AndZeroForMissingKey()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        (await store.GetMessageCountAsync(key)).ShouldBe(0);

        await store.AppendMessagesAsync(key,
            [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, "b")]);

        (await store.GetMessageCountAsync(key)).ShouldBe(2);
    }
}