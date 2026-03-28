using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryDreamingServiceTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IMemoryConsolidator> _consolidator = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly MemoryDreamingOptions _options = new();
    private readonly MemoryDreamingService _service;

    private static readonly DateTimeOffset Now = new(2026, 3, 28, 3, 0, 0, TimeSpan.Zero);

    public MemoryDreamingServiceTests()
    {
        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);

        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _consolidator
            .Setup(c => c.SynthesizeProfileAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalityProfile
            {
                UserId = "user1",
                Summary = "Test profile",
                LastUpdated = Now
            });

        _store
            .Setup(s => s.SaveProfileAsync(It.IsAny<PersonalityProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile p, CancellationToken _) => p);

        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new MemoryDreamingService(
            _store.Object,
            _consolidator.Object,
            _embeddingService.Object,
            _metricsPublisher.Object,
            Mock.Of<ICronValidator>(),
            NullLogger<MemoryDreamingService>.Instance,
            _options);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_ExecutesMergeThenDecayThenReflect()
    {
        var callOrder = new List<string>();

        var memories = new List<MemoryEntry>
        {
            MakeMemory("m1", "user1", MemoryCategory.Fact, 0.8, Now.AddDays(-5), Now.AddDays(-5))
        };

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("consolidate"))
            .ReturnsAsync([]);

        _consolidator
            .Setup(c => c.SynthesizeProfileAsync("user1", It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("synthesize"))
            .ReturnsAsync(new PersonalityProfile
            {
                UserId = "user1",
                Summary = "Test",
                LastUpdated = Now
            });

        await _service.RunDreamingForUserAsync("user1", Now, CancellationToken.None);

        callOrder.Count.ShouldBe(2);
        callOrder[0].ShouldBe("consolidate");
        callOrder[1].ShouldBe("synthesize");
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecaysOldUnaccesedMemories()
    {
        var oldFact = MakeMemory("old_fact", "user1", MemoryCategory.Fact, 0.8,
            Now.AddDays(-60), Now.AddDays(-60));
        var recentFact = MakeMemory("recent_fact", "user1", MemoryCategory.Fact, 0.8,
            Now.AddDays(-5), Now.AddDays(-5));
        var oldInstruction = MakeMemory("old_instr", "user1", MemoryCategory.Instruction, 0.8,
            Now.AddDays(-60), Now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldFact, recentFact, oldInstruction });

        await _service.RunDreamingForUserAsync("user1", Now, CancellationToken.None);

        // Old fact should be decayed: 0.8 * 0.9 = 0.72
        _store.Verify(s => s.UpdateImportanceAsync("user1", "old_fact", 0.72, It.IsAny<CancellationToken>()), Times.Once);

        // Recent fact should NOT be decayed
        _store.Verify(s => s.UpdateImportanceAsync("user1", "recent_fact", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);

        // Old instruction should NOT be decayed (exempt category)
        _store.Verify(s => s.UpdateImportanceAsync("user1", "old_instr", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecayRespectsFloor()
    {
        // importance 0.05 * 0.9 = 0.045 < floor 0.1 → NOT decayed
        var lowImportance = MakeMemory("low", "user1", MemoryCategory.Fact, 0.05,
            Now.AddDays(-60), Now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { lowImportance });

        await _service.RunDreamingForUserAsync("user1", Now, CancellationToken.None);

        _store.Verify(s => s.UpdateImportanceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_AppliesMergeDecisions()
    {
        var m1 = MakeMemory("m1", "user1", MemoryCategory.Fact, 0.7, Now.AddDays(-10), Now.AddDays(-10));
        var m2 = MakeMemory("m2", "user1", MemoryCategory.Fact, 0.8, Now.AddDays(-5), Now.AddDays(-5));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1, m2 });

        var mergeDecision = new MergeDecision(
            SourceIds: ["m1", "m2"],
            Action: MergeAction.Merge,
            MergedContent: "Combined fact about user",
            Category: MemoryCategory.Fact,
            Importance: 0.85,
            Tags: ["merged"]);

        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mergeDecision]);

        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync("Combined fact about user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        await _service.RunDreamingForUserAsync("user1", Now, CancellationToken.None);

        // Should store a new merged memory
        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m =>
                m.Content == "Combined fact about user" &&
                m.Category == MemoryCategory.Fact &&
                m.Importance == 0.85),
            It.IsAny<CancellationToken>()), Times.Once);

        // Should supersede both source IDs to the new memory
        _store.Verify(s => s.SupersedeAsync("user1", "m1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SupersedeAsync("user1", "m2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_PublishesDreamingMetric()
    {
        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        await _service.RunDreamingForUserAsync("user1", Now, CancellationToken.None);

        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryDreamingEvent>();
        var dreamingEvent = (MemoryDreamingEvent)published;
        dreamingEvent.UserId.ShouldBe("user1");
        dreamingEvent.ProfileRegenerated.ShouldBeTrue();
    }

    private static MemoryEntry MakeMemory(
        string id, string userId, MemoryCategory category, double importance,
        DateTimeOffset createdAt, DateTimeOffset lastAccessedAt) =>
        new()
        {
            Id = id,
            UserId = userId,
            Category = category,
            Content = $"Memory {id}",
            Importance = importance,
            Confidence = 0.9,
            CreatedAt = createdAt,
            LastAccessedAt = lastAccessedAt
        };
}
