using System.Text.Json;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Shouldly;
using StackExchange.Redis;
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
    public async Task GetMessagesAsync_LegacyStringKey_ReturnsMessages()
    {
        var key = $"thread-legacy-{Guid.NewGuid():N}";
        var legacyJson = JsonSerializer.Serialize(new
        {
            Messages = new[]
            {
                new ChatMessage(ChatRole.User, "legacy question"),
                new ChatMessage(ChatRole.Assistant, "legacy answer")
            }
        });
        await redisFixture.Connection.GetDatabase().StringSetAsync(key, legacyJson);

        var messages = await NewStore().GetMessagesAsync(key);

        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["legacy question", "legacy answer"]);
    }

    [Fact]
    public async Task AppendMessagesAsync_OnLegacyStringKey_MigratesThenAppends()
    {
        var key = $"thread-legacy-{Guid.NewGuid():N}";
        var legacyJson = JsonSerializer.Serialize(new
        {
            Messages = new[] { new ChatMessage(ChatRole.User, "old turn") }
        });
        var db = redisFixture.Connection.GetDatabase();
        await db.StringSetAsync(key, legacyJson);

        var store = NewStore();
        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.Assistant, "new turn")]);

        (await db.KeyTypeAsync(key)).ShouldBe(RedisType.List);
        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["old turn", "new turn"]);
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
}