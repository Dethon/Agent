using Domain.Agents;
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using McpChannelSignalR.Services;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelSignalR;

public class RedisStateServiceTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private readonly RedisStateService _sut = new(redis.Connection);

    [Fact]
    public async Task GetAllTopicsAsync_FiltersBySpaceSlug()
    {
        var topic1 = new TopicMetadata("t-s1", 300, 0, "agent-slug", "Space1", DateTimeOffset.UtcNow, null, SpaceSlug: "space-a");
        var topic2 = new TopicMetadata("t-s2", 301, 0, "agent-slug", "Space2", DateTimeOffset.UtcNow, null, SpaceSlug: "space-b");

        await _sut.SaveTopicAsync(topic1);
        await _sut.SaveTopicAsync(topic2);

        var filtered = await _sut.GetAllTopicsAsync("agent-slug", "space-a");
        filtered.ShouldContain(t => t.TopicId == "t-s1");
        filtered.ShouldNotContain(t => t.TopicId == "t-s2");
    }

    [Fact]
    public async Task GetHistoryAsync_ReadsNewRedisListFormat()
    {
        const string agentId = "agent-hist";
        const long chatId = 900;
        const long threadId = 0;
        var key = new AgentKey($"{chatId}:{threadId}", agentId).ToString();

        // The agent now persists history as a Redis List via RedisThreadStateStore.
        var store = new RedisThreadStateStore(redis.Connection, TimeSpan.FromMinutes(5));
        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "hello there"),
            new ChatMessage(ChatRole.Assistant, "hi, how can I help?")
        ]);

        var history = await _sut.GetHistoryAsync(agentId, chatId, threadId);

        history.Select(h => h.Content).ShouldBe(["hello there", "hi, how can I help?"]);
    }
}