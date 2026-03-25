using Domain.Agents;
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class NullThreadStateStoreTests
{
    private readonly NullThreadStateStore _store = new();

    [Fact]
    public async Task GetMessagesAsync_ReturnsNull()
    {
        var result = await _store.GetMessagesAsync("any-key");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetMessagesAsync_DoesNotThrow()
    {
        await _store.SetMessagesAsync("key", [new ChatMessage(ChatRole.User, "hi")]);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow()
    {
        await _store.DeleteAsync(new AgentKey("test"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse()
    {
        var result = await _store.ExistsAsync("key");
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAllTopicsAsync_ReturnsEmpty()
    {
        var result = await _store.GetAllTopicsAsync("agent-id");
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveTopicAsync_DoesNotThrow()
    {
        var topic = new TopicMetadata("t", 1, 1, "a", "test", DateTimeOffset.UtcNow, null);
        await _store.SaveTopicAsync(topic);
    }

    [Fact]
    public async Task DeleteTopicAsync_DoesNotThrow()
    {
        await _store.DeleteTopicAsync("agent", 1, "topic");
    }

    [Fact]
    public async Task GetTopicByChatIdAndThreadIdAsync_ReturnsNull()
    {
        var result = await _store.GetTopicByChatIdAndThreadIdAsync("agent", 1, 1);
        result.ShouldBeNull();
    }
}
