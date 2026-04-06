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

    private static readonly DateTimeOffset _now = new(2026, 3, 28, 3, 0, 0, TimeSpan.Zero);

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
                LastUpdated = _now
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
            MakeMemory("m1", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5))
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
                LastUpdated = _now
            });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        callOrder.Count.ShouldBe(2);
        callOrder[0].ShouldBe("consolidate");
        callOrder[1].ShouldBe("synthesize");
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecaysOldUnaccesedMemories()
    {
        var oldFact = MakeMemory("old_fact", "user1", MemoryCategory.Fact, 0.8,
            _now.AddDays(-60), _now.AddDays(-60));
        var recentFact = MakeMemory("recent_fact", "user1", MemoryCategory.Fact, 0.8,
            _now.AddDays(-5), _now.AddDays(-5));
        var oldInstruction = MakeMemory("old_instr", "user1", MemoryCategory.Instruction, 0.8,
            _now.AddDays(-60), _now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldFact, recentFact, oldInstruction });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

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
            _now.AddDays(-60), _now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { lowImportance });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.UpdateImportanceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_AppliesMergeDecisions()
    {
        var m1 = MakeMemory("m1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m2 = MakeMemory("m2", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5));

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
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mergeDecision])
            .ReturnsAsync([]);

        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync("Combined fact about user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        // Should store a new merged memory
        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m =>
                m.Content == "Combined fact about user" &&
                m.Category == MemoryCategory.Fact &&
                m.Importance == 0.85),
            It.IsAny<CancellationToken>()), Times.Once);

        // Should delete both source memories after merge
        _store.Verify(s => s.DeleteAsync("user1", "m1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync("user1", "m2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_SupersedeOlder_DeletesOldMemory()
    {
        var oldMemory = MakeMemory("old", "user1", MemoryCategory.Fact, 0.5, _now.AddDays(-30), _now.AddDays(-30));
        var newMemory = MakeMemory("new", "user1", MemoryCategory.Fact, 0.9, _now.AddDays(-1), _now.AddDays(-1));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldMemory, newMemory });

        var supersedeDecision = new MergeDecision(
            SourceIds: ["old", "new"],
            Action: MergeAction.SupersedeOlder);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([supersedeDecision])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        // Should delete the old (superseded) memory
        _store.Verify(s => s.DeleteAsync("user1", "old", It.IsAny<CancellationToken>()), Times.Once);

        // Should NOT delete the new (surviving) memory
        _store.Verify(s => s.DeleteAsync("user1", "new", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_LoopsMergePassUntilConsolidatorReturnsEmpty()
    {
        var m1 = MakeMemory("m1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m2 = MakeMemory("m2", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m3 = MakeMemory("m3", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));

        // After first merge, m1+m2 are deleted and a new memory appears.
        // Second consolidation pass has the chance to merge the survivor with m3.
        var afterFirstMerge = new List<MemoryEntry>
        {
            m3,
            MakeMemory("merged1", "user1", MemoryCategory.Fact, 0.85, _now, _now)
        };
        var afterSecondMerge = new List<MemoryEntry>
        {
            MakeMemory("merged2", "user1", MemoryCategory.Fact, 0.9, _now, _now)
        };

        _store
            .SetupSequence(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1, m2, m3 })
            .ReturnsAsync(afterFirstMerge)
            .ReturnsAsync(afterSecondMerge)
            .ReturnsAsync(afterSecondMerge);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MergeDecision(["m1", "m2"], MergeAction.Merge, "merged", MemoryCategory.Fact, 0.85, [])
            ])
            .ReturnsAsync([
                new MergeDecision(["merged1", "m3"], MergeAction.Merge, "merged again", MemoryCategory.Fact, 0.9, [])
            ])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _consolidator.Verify(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "merged"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "merged again"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_MergeLoopIsBoundedByMaxPasses()
    {
        var memories = new List<MemoryEntry>
        {
            MakeMemory("a", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10)),
            MakeMemory("b", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10))
        };

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        // Consolidator never returns empty — only the bound should stop the loop.
        // Use fresh (valid) source IDs on every pass by referencing the ones the store returns.
        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntry> list, CancellationToken _) =>
                list.Count >= 2
                    ? [new MergeDecision([list[0].Id, list[1].Id], MergeAction.Merge, "x", MemoryCategory.Fact, 0.8, [])]
                    : []);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _consolidator.Verify(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()),
            Times.AtMost(_options.MaxMergePasses));
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

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

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
