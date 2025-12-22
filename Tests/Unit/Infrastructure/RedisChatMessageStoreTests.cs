using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class RedisChatMessageStoreTests
{
    [Fact]
    public async Task CreateAsync_WithAgentKey_UsesCorrectRedisKey()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var agentKey = new AgentKey(123, 456);
        var serializedState = JsonSerializer.SerializeToElement(agentKey);
        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        // Act
        await RedisChatMessageStore.CreateAsync(mockStore.Object, ctx);

        // Assert
        mockStore.Verify(s => s.GetMessagesAsync("thread:123:456"), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithUndefinedState_UsesGuidKey()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = default, // JsonValueKind.Undefined
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        // Act
        await RedisChatMessageStore.CreateAsync(mockStore.Object, ctx);

        // Assert - Should have called GetMessagesAsync with a GUID-like key
        mockStore.Verify(s => s.GetMessagesAsync(It.Is<string>(k => IsGuid(k))), Times.Once);
    }

    private static bool IsGuid(string value)
    {
        return Guid.TryParse(value, out _);
    }

    [Fact]
    public async Task Serialize_ReturnsKeyAsJsonElement()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var agentKey = new AgentKey(123, 456);
        var serializedState = JsonSerializer.SerializeToElement(agentKey);
        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.CreateAsync(mockStore.Object, ctx);

        // Act
        var serialized = store.Serialize();

        // Assert - The serialized value is a string (the redis key), not an AgentKey
        serialized.ValueKind.ShouldBe(JsonValueKind.String);
        serialized.GetString().ShouldBe("thread:123:456");
    }

    [Fact]
    public async Task CreateAsync_WithSerializedStringKey_UsesStringKeyDirectly()
    {
        // Arrange - This tests what happens when we restore from a serialized thread
        // The serialized state will be a string like "thread:123:456"
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        // Simulate what happens when the serialized store state (a string) is passed back
        const string stringKey = "thread:123:456";
        var serializedState = JsonSerializer.SerializeToElement(stringKey);
        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        // Act - Should use the string key directly
        await RedisChatMessageStore.CreateAsync(mockStore.Object, ctx);

        // Assert - The string key should be used directly
        mockStore.Verify(s => s.GetMessagesAsync("thread:123:456"), Times.Once);
    }

    [Fact]
    public void GetRedisKey_ReturnsCorrectFormat()
    {
        // Arrange
        var agentKey = new AgentKey(999, 888);

        // Act
        var redisKey = RedisChatMessageStore.GetRedisKey(agentKey);

        // Assert
        redisKey.ShouldBe("thread:999:888");
    }
}