using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryForgetToolTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    private MemoryForgetTool CreateTool() => new(_store.Object, _embedding.Object);

    [Fact]
    public async Task Run_WithQuery_GeneratesEmbeddingAndUsesSearchAsync()
    {
        var userId = "user1";
        var query = "my job";
        var fakeEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memory = CreateMemory("mem1", "I work at Acme Corp", MemoryCategory.Fact);

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query);

        _embedding.Verify(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SearchAsync(
            userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()), Times.Once);
        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    }

    private static MemoryEntry CreateMemory(
        string id, string content, MemoryCategory category,
        double importance = 0.5, DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = id,
            UserId = "user1",
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.8,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Tags = []
        };
}
