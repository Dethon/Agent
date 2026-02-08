using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.Extensions;
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
        var ctx = new ChatClientAgentOptions.ChatHistoryProviderFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.Create(mockStore.Object, ctx);

        // Act
        await store.InvokingAsync(new ChatHistoryProvider.InvokingContext(new Mock<AIAgent>().Object, null, []), CancellationToken.None);

        // Assert
        mockStore.Verify(s => s.GetMessagesAsync(agentKey.ToString()), Times.Once);
    }

    [Fact]
    public async Task InvokingAsync_WithUndefinedState_UsesGuidKey()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var ctx = new ChatClientAgentOptions.ChatHistoryProviderFactoryContext
        {
            SerializedState = default, // JsonValueKind.Undefined
            JsonSerializerOptions = new JsonSerializerOptions()
        };

        var store = await RedisChatMessageStore.Create(mockStore.Object, ctx);

        // Act
        await store.InvokingAsync(new ChatHistoryProvider.InvokingContext(new Mock<AIAgent>().Object, null, []), CancellationToken.None);

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
        var ctx = new ChatClientAgentOptions.ChatHistoryProviderFactoryContext
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

    [Fact]
    public void ToChatResponse_DoesNotPreserveAdditionalPropertiesOnMessages()
    {
        // Documents framework behavior: ToChatResponse drops AdditionalProperties
        // from streaming updates when building ChatMessage objects.
        // This is why RedisChatMessageStore must stamp timestamps itself.
        var updates = new List<ChatResponseUpdate>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Hello")],
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["Timestamp"] = DateTimeOffset.UtcNow
                }
            }
        };

        var response = updates.ToChatResponse();

        var message = response.Messages.ShouldHaveSingleItem();
        message.GetTimestamp().ShouldBeNull();
    }

    [Fact]
    public async Task InvokedAsync_ResponseMessages_HaveTimestampAfterStorage()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        ChatMessage[]? savedMessages = null;
        mockStore.Setup(s => s.SetMessagesAsync(It.IsAny<string>(), It.IsAny<ChatMessage[]>()))
            .Callback<string, ChatMessage[]>((_, msgs) => savedMessages = msgs)
            .Returns(Task.CompletedTask);

        var store = await CreateStore(mockStore.Object);

        // Simulate what the framework produces: response messages without timestamps
        // (ToChatResponse drops AdditionalProperties from streaming updates)
        var responseMessage = new ChatMessage(ChatRole.Assistant, "Hello from agent");

        var invokedContext = new ChatHistoryProvider.InvokedContext(
            new Mock<AIAgent>().Object, null, [new ChatMessage(ChatRole.User, "Hi")], [])
        {
            ResponseMessages = [responseMessage]
        };

        // Act
        await store.InvokedAsync(invokedContext, CancellationToken.None);

        // Assert
        savedMessages.ShouldNotBeNull();
        var assistantMsg = savedMessages.First(m => m.Role == ChatRole.Assistant);
        assistantMsg.GetTimestamp().ShouldNotBeNull();
    }

    private static async Task<ChatHistoryProvider> CreateStore(IThreadStateStore threadStore)
    {
        var agentKey = new AgentKey(123, 456);
        var serializedState = JsonSerializer.SerializeToElement(agentKey.ToString());
        var ctx = new ChatClientAgentOptions.ChatHistoryProviderFactoryContext
        {
            SerializedState = serializedState,
            JsonSerializerOptions = new JsonSerializerOptions()
        };
        return await RedisChatMessageStore.Create(threadStore, ctx);
    }
}