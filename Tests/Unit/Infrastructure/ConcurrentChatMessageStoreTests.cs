using System.Text.Json;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ConcurrentChatMessageStoreTests
{
    [Fact]
    public async Task GetMessagesAsync_WhenEmpty_ReturnsEmptyCollection()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();

        // Act
        var messages = await store.GetMessagesAsync();

        // Assert
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddMessagesAsync_AddsMessages()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hi there")
        };

        // Act
        await store.AddMessagesAsync(messages);
        var result = await store.GetMessagesAsync();

        // Assert
        result.Count().ShouldBe(2);
    }

    [Fact]
    public async Task AddMessagesAsync_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() => store.AddMessagesAsync(null!));
    }

    [Fact]
    public async Task AddMessagesAsync_MultipleCalls_AccumulatesMessages()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();

        // Act
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "First")]);
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "Second")]);
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "Third")]);
        var result = await store.GetMessagesAsync();

        // Assert
        result.Count().ShouldBe(3);
    }

    [Fact]
    public async Task Serialize_ReturnsJsonElement()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "Test message")]);

        // Act
        var serialized = store.Serialize();

        // Assert
        serialized.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task Constructor_WithSerializedState_RestoresMessages()
    {
        // Arrange
        var originalStore = new ConcurrentChatMessageStore();
        await originalStore.AddMessagesAsync([
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "World")
        ]);
        var serialized = originalStore.Serialize();

        // Act
        var restoredStore = new ConcurrentChatMessageStore(serialized);
        var messages = (await restoredStore.GetMessagesAsync()).ToArray();

        // Assert
        messages.Length.ShouldBe(2);
        messages[0].Role.ShouldBe(ChatRole.User);
        messages[1].Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public async Task Constructor_WithInvalidJsonElement_CreatesEmptyStore()
    {
        // Arrange
        var invalidJson = JsonSerializer.SerializeToElement("not an object");

        // Act
        var store = new ConcurrentChatMessageStore(invalidJson);
        var messages = await store.GetMessagesAsync();

        // Assert
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Constructor_WithEmptyObject_CreatesEmptyStore()
    {
        // Arrange
        var emptyObject = JsonSerializer.SerializeToElement(new { });

        // Act
        var store = new ConcurrentChatMessageStore(emptyObject);
        var messages = await store.GetMessagesAsync();

        // Assert
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentAddMessages_MaintainsAllMessages()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();
        const int messageCount = 100;

        // Act - simulate concurrent access
        var tasks = Enumerable.Range(0, messageCount).Select(i =>
            store.AddMessagesAsync([new ChatMessage(ChatRole.User, $"Message {i}")]));
        await Task.WhenAll(tasks);

        var result = await store.GetMessagesAsync();

        // Assert
        result.Count().ShouldBe(messageCount);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsImmutableSnapshot()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "First")]);

        // Act
        var snapshot1 = (await store.GetMessagesAsync()).ToArray();
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "Second")]);
        var snapshot2 = (await store.GetMessagesAsync()).ToArray();

        // Assert - first snapshot should not be affected by subsequent adds
        snapshot1.Length.ShouldBe(1);
        snapshot2.Length.ShouldBe(2);
    }

    [Fact]
    public async Task Serialize_WithCustomOptions_UsesOptions()
    {
        // Arrange
        var store = new ConcurrentChatMessageStore();
        await store.AddMessagesAsync([new ChatMessage(ChatRole.User, "Test")]);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Act
        var serialized = store.Serialize(options);

        // Assert
        serialized.ValueKind.ShouldBe(JsonValueKind.Object);
    }
}