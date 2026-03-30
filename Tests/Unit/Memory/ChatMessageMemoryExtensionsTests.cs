using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class ChatMessageMemoryExtensionsTests
{
    [Fact]
    public void SetMemoryContext_AndGetMemoryContext_RoundTrips()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var context = new MemoryContext(
        [
            new MemorySearchResult(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Likes concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.95)
        ], null);

        message.SetMemoryContext(context);
        var retrieved = message.GetMemoryContext();

        retrieved.ShouldNotBeNull();
        retrieved.Memories.Count.ShouldBe(1);
        retrieved.Memories[0].Memory.Content.ShouldBe("Likes concise responses");
    }

    [Fact]
    public void GetMemoryContext_WhenNotSet_ReturnsNull()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var retrieved = message.GetMemoryContext();
        retrieved.ShouldBeNull();
    }

    [Fact]
    public void SetMemoryContext_WithNull_DoesNotThrow()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        message.SetMemoryContext(null);
        message.GetMemoryContext().ShouldBeNull();
    }
}
