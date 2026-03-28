using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterChatClientMemoryInjectionTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_WithMemoryContext_PrependsMemoryBlock()
    {
        var innerClient = new Mock<IChatClient>();
        var responseUpdates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("Hello!")] }
        };

        var message = new ChatMessage(ChatRole.User, "Help me");
        message.SetSenderId("user1");
        message.SetTimestamp(DateTimeOffset.UtcNow);
        message.SetMemoryContext(new MemoryContext(
        [
            new MemorySearchResult(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "User prefers concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.92)
        ], null));

        IEnumerable<ChatMessage>? capturedMessages = null;
        innerClient.Setup(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .Returns(responseUpdates.ToAsyncEnumerable());

        var client = new OpenRouterChatClient(innerClient.Object, "test-model");

        await foreach (var _ in client.GetStreamingResponseAsync([message]))
        { }

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.First(m => m.Role == ChatRole.User);
        var textContents = userMsg.Contents.OfType<TextContent>().Select(t => t.Text).ToList();
        var fullText = string.Join("", textContents);
        fullText.ShouldContain("[Memory context]");
        fullText.ShouldContain("User prefers concise responses");
        fullText.ShouldContain("[End memory context]");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutMemoryContext_NoMemoryBlock()
    {
        var innerClient = new Mock<IChatClient>();
        var responseUpdates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("Hello!")] }
        };

        var message = new ChatMessage(ChatRole.User, "Help me");
        message.SetSenderId("user1");

        IEnumerable<ChatMessage>? capturedMessages = null;
        innerClient.Setup(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .Returns(responseUpdates.ToAsyncEnumerable());

        var client = new OpenRouterChatClient(innerClient.Object, "test-model");

        await foreach (var _ in client.GetStreamingResponseAsync([message]))
        { }

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.First(m => m.Role == ChatRole.User);
        var fullText = string.Join("", userMsg.Contents.OfType<TextContent>().Select(t => t.Text));
        fullText.ShouldNotContain("[Memory context]");
    }
}
