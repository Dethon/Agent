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

    [Fact]
    public async Task Run_WithTags_PassesTagsToSearchAsync()
    {
        var userId = "user1";
        var query = "some query";
        var parsedTags = new List<string> { "work", "project" };
        var fakeEmbedding = new float[] { 0.1f };
        var memory = CreateMemory("mem1", "Work project info", MemoryCategory.Project);

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding, null,
                It.Is<IEnumerable<string>>(t => t.SequenceEqual(parsedTags)),
                null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query, tags: "work,project");

        _store.Verify(s => s.SearchAsync(
            userId, query, fakeEmbedding, null,
            It.Is<IEnumerable<string>>(t => t.SequenceEqual(parsedTags)),
            null, 100, It.IsAny<CancellationToken>()), Times.Once);
        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithMaxImportance_FiltersOutHighImportanceMemories()
    {
        var userId = "user1";
        var query = "stuff";
        var fakeEmbedding = new float[] { 0.1f };
        var lowImportance = CreateMemory("mem1", "Low importance", MemoryCategory.Event, importance: 0.3);
        var highImportance = CreateMemory("mem2", "High importance", MemoryCategory.Instruction, importance: 0.9);

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemorySearchResult(lowImportance, 0.8),
                new MemorySearchResult(highImportance, 0.7)
            ]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query, maxImportance: 0.5);

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        _store.Verify(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync(userId, "mem2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithOlderThan_FiltersOutRecentMemories()
    {
        var userId = "user1";
        var query = "stuff";
        var fakeEmbedding = new float[] { 0.1f };
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var oldMemory = CreateMemory("mem1", "Old memory", MemoryCategory.Fact, createdAt: cutoff.AddDays(-1));
        var newMemory = CreateMemory("mem2", "New memory", MemoryCategory.Fact, createdAt: cutoff.AddDays(1));

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemorySearchResult(oldMemory, 0.8),
                new MemorySearchResult(newMemory, 0.7)
            ]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query, olderThan: cutoff.ToString("O"));

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        _store.Verify(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync(userId, "mem2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithCategories_PassesCategoriesToSearchAsync()
    {
        var userId = "user1";
        var query = "stuff";
        var fakeEmbedding = new float[] { 0.1f };
        var memory = CreateMemory("mem1", "A preference", MemoryCategory.Preference);

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding,
                It.Is<IEnumerable<MemoryCategory>>(c => c.SequenceEqual(new[] { MemoryCategory.Preference })),
                null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query, categories: "Preference");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithMemoryId_StillUsesDirectLookup()
    {
        var userId = "user1";
        var memory = CreateMemory("mem1", "Direct lookup", MemoryCategory.Fact);

        _store.Setup(s => s.GetByIdAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memory);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, memoryId: "mem1");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        _embedding.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SearchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]?>(),
            It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(),
            It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_NoMemoryIdOrQuery_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.Run("user1");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldBe("Either memoryId or query must be provided");
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
