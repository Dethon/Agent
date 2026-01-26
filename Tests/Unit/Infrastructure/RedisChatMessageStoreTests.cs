using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class RedisChatMessageStoreTests
{
    [Fact]
    public async Task InvokingAsync_WithStringKey_UsesKeyDirectly()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var agentKey = new AgentKey(123, 456);
        var serializedState = JsonSerializer.SerializeToElement(agentKey.ToString());
        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.Create(mockStore.Object, ctx);

        // Act
        await store.InvokingAsync(new ChatMessageStore.InvokingContext([]), CancellationToken.None);

        // Assert
        mockStore.Verify(s => s.GetMessagesAsync(agentKey.ToString()), Times.Once);
    }

    [Fact]
    public async Task InvokingAsync_WithUndefinedState_UsesGuidKey()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = default, // JsonValueKind.Undefined
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.Create(mockStore.Object, ctx);

        // Act
        await store.InvokingAsync(new ChatMessageStore.InvokingContext([]), CancellationToken.None);

        // Assert
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
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var agentKey = new AgentKey(123, 456);
        var serializedState = JsonSerializer.SerializeToElement(agentKey.ToString());
        var ctx = new ChatClientAgentOptions.ChatMessageStoreFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.Create(mockStore.Object, ctx);

        // Act
        var serialized = store.Serialize();

        // Assert
        serialized.ValueKind.ShouldBe(JsonValueKind.String);
        serialized.GetString().ShouldBe(agentKey.ToString());
    }

    [Fact]
    public void AgentKey_ToString_ReturnsCorrectFormat()
    {
        // Arrange
        var agentKey = new AgentKey(999, 888, "test-agent");

        // Act
        var key = agentKey.ToString();

        // Assert
        key.ShouldBe("agent-key:test-agent:999:888");
    }
}