using Infrastructure.Storage;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure;

public class RedisConversationHistoryStoreTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task SaveAsync_AndGetAsync_RoundTripsMessages()
    {
        // Arrange
        var conversationId = $"test-{Guid.NewGuid()}";
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.User, "Hello!"), new ChatMessage(ChatRole.Assistant, "Hi there!")
        };
        var serialized = ChatMessageSerializer.Serialize(messages);

        // Act
        await redisFixture.Store.SaveAsync(conversationId, serialized, CancellationToken.None);
        var retrieved = await redisFixture.Store.GetAsync(conversationId, CancellationToken.None);

        // Assert
        retrieved.ShouldNotBeNull();
        var deserialized = ChatMessageSerializer.Deserialize(retrieved);
        deserialized.Length.ShouldBe(3);
        deserialized[0].Role.ShouldBe(ChatRole.System);
        deserialized[1].Role.ShouldBe(ChatRole.User);
        deserialized[2].Role.ShouldBe(ChatRole.Assistant);
        deserialized[1].Text.ShouldBe("Hello!");
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ReturnsNull()
    {
        // Arrange
        var conversationId = $"nonexistent-{Guid.NewGuid()}";

        // Act
        var result = await redisFixture.Store.GetAsync(conversationId, CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesConversation()
    {
        // Arrange
        var conversationId = $"delete-test-{Guid.NewGuid()}";
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };
        var serialized = ChatMessageSerializer.Serialize(messages);
        await redisFixture.Store.SaveAsync(conversationId, serialized, CancellationToken.None);

        // Act
        await redisFixture.Store.DeleteAsync(conversationId, CancellationToken.None);
        var result = await redisFixture.Store.GetAsync(conversationId, CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_WithToolCalls_PreservesToolCallContent()
    {
        // Arrange
        var conversationId = $"toolcall-{Guid.NewGuid()}";
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Search for movies"), new ChatMessage(ChatRole.Assistant,
            [
                new TextContent("I'll search for that."),
                new FunctionCallContent("call_123", "search",
                    new Dictionary<string, object?>
                    {
                        ["query"] = "movies"
                    })
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call_123", """{"results": ["Movie 1", "Movie 2"]}""")
            ])
        };
        var serialized = ChatMessageSerializer.Serialize(messages);

        // Act
        await redisFixture.Store.SaveAsync(conversationId, serialized, CancellationToken.None);
        var retrieved = await redisFixture.Store.GetAsync(conversationId, CancellationToken.None);

        // Assert
        retrieved.ShouldNotBeNull();
        var deserialized = ChatMessageSerializer.Deserialize(retrieved);
        deserialized.Length.ShouldBe(3);

        var assistantMessage = deserialized[1];
        assistantMessage.Contents.Count.ShouldBe(2);
        assistantMessage.Contents[0].ShouldBeOfType<TextContent>();
        assistantMessage.Contents[1].ShouldBeOfType<FunctionCallContent>();

        var toolMessage = deserialized[2];
        toolMessage.Contents[0].ShouldBeOfType<FunctionResultContent>();
    }
}