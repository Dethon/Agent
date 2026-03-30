using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

[Trait("Category", "Integration")]
public class MemoryRecallHookIntegrationTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task EnrichAsync_WithStoredMemories_InjectsContextIntoMessage()
    {
        var store = new RedisStackMemoryStore(redisFixture.Connection);
        var embeddingService = new Mock<IEmbeddingService>();
        var queue = new MemoryExtractionQueue();
        var metricsPublisher = new Mock<IMetricsPublisher>();

        var userId = $"user_{Guid.NewGuid():N}";
        var embedding = CreateTestEmbedding();

        // Store a memory
        await store.StoreAsync(new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Preference,
            Content = "User prefers TypeScript over JavaScript",
            Importance = 0.9,
            Confidence = 0.8,
            Embedding = embedding,
            Tags = ["language"],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        });

        embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        var hook = new MemoryRecallHook(
            store, embeddingService.Object, queue, metricsPublisher.Object,
            Mock.Of<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());

        var message = new ChatMessage(ChatRole.User, "What language should I use?");

        await hook.EnrichAsync(message, userId, "conv_1", null, CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.Count.ShouldBeGreaterThan(0);
        context.Memories.ShouldContain(m => m.Memory.Content.Contains("TypeScript"));
    }

    private static float[] CreateTestEmbedding()
    {
        var rng = new Random(42);
        return Enumerable.Range(0, 1536).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
    }
}
