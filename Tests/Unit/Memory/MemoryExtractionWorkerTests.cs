using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryExtractionWorkerTests
{
    private readonly Mock<IMemoryExtractor> _extractor = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryExtractionOptions _options = new();
    private readonly MemoryExtractionWorker _worker;

    public MemoryExtractionWorkerTests()
    {
        _worker = new MemoryExtractionWorker(
            _queue,
            _extractor.Object,
            _embeddingService.Object,
            _store.Object,
            _threadStateStore.Object,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            NullLogger<MemoryExtractionWorker>.Instance,
            _options);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithNovelCandidate_StoresMemory()
    {
        var candidate = new ExtractionCandidate(
            Content: "User works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: ["work", "company"],
            Context: "Mentioned during introduction");

        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-1"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Hello, I work at Contoso")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w =>
                    w.Count == 1 && w[0].Text == "Hello, I work at Contoso"),
                "user1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(candidate.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _store
            .Setup(s => s.SearchAsync("user1", null, embedding,
                It.Is<IEnumerable<MemoryCategory>>(c => c.Contains(MemoryCategory.Fact)),
                null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);

        var request = new MemoryExtractionRequest("user1", "thread-key-1", 0, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m =>
                m.UserId == "user1" &&
                m.Content == "User works at Contoso" &&
                m.Category == MemoryCategory.Fact &&
                m.Importance == 0.8 &&
                m.Confidence == 0.9 &&
                m.Source != null &&
                m.Source.ConversationId == "conv_1" &&
                m.Id.StartsWith("mem_")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithDuplicateCandidate_SkipsStore()
    {
        var candidate = new ExtractionCandidate(
            Content: "User works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: [],
            Context: null);

        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-2"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "I work at Contoso")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        var existingMemory = new MemoryEntry
        {
            Id = "mem_existing",
            UserId = "user1",
            Category = MemoryCategory.Fact,
            Content = "Works at Contoso",
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        _store
            .Setup(s => s.SearchAsync("user1", null, embedding,
                It.IsAny<IEnumerable<MemoryCategory>>(),
                null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(existingMemory, 0.92)]);

        var request = new MemoryExtractionRequest("user1", "thread-key-2", 0, null, null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_PublishesExtractionMetric()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-3"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Some message")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        var request = new MemoryExtractionRequest("user2", "thread-key-3", 0, null, null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryExtractionEvent>();
        var extractionEvent = (MemoryExtractionEvent)published;
        extractionEvent.UserId.ShouldBe("user2");
        extractionEvent.CandidateCount.ShouldBe(0);
        extractionEvent.StoredCount.ShouldBe(0);
        extractionEvent.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_SkipsExtraction_WhenAgentDoesNotHaveMemoryFeature()
    {
        _agentDefinitionProvider.Setup(p => p.GetById("agent-no-memory"))
            .Returns(new AgentDefinition
            {
                Id = "agent-no-memory", Name = "NoMem", Model = "test",
                McpServerEndpoints = [], EnabledFeatures = ["scheduling"]
            });

        var request = new MemoryExtractionRequest("user1", "any-key", 0, "conv_1", "agent-no-memory");

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _metricsPublisher.Verify(
            p => p.PublishAsync(It.IsAny<MemoryExtractionEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenExtractorFails_PublishesErrorEvent()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-5"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Some message")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        var request = new MemoryExtractionRequest("user3", "thread-key-5", 0, null, null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        published.ShouldNotBeNull();
        published.ShouldBeOfType<ErrorEvent>();
        var errorEvent = (ErrorEvent)published;
        errorEvent.Service.ShouldBe("memory");
        errorEvent.ErrorType.ShouldBe(nameof(HttpRequestException));
        errorEvent.Message.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task ProcessRequestAsync_WithWindow_PassesLastMTurnsToExtractor()
    {
        var stateKey = "state-key-window";
        var allMessages = new ChatMessage[]
        {
            new(ChatRole.User, "turn1 user"),
            new(ChatRole.Assistant, "turn1 assistant"),
            new(ChatRole.User, "turn2 user"),
            new(ChatRole.Assistant, "turn2 assistant"),
            new(ChatRole.User, "turn3 user"),
            new(ChatRole.Assistant, "turn3 assistant"),
            new(ChatRole.User, "turn4 user"),
            new(ChatRole.Assistant, "turn4 assistant"),
            new(ChatRole.User, "turn5 user (drift)")
        };

        _threadStateStore.Setup(s => s.GetMessagesAsync(stateKey))
            .ReturnsAsync(allMessages);

        IReadOnlyList<ChatMessage>? capturedWindow = null;
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>((w, _, _) => capturedWindow = w)
            .ReturnsAsync([]);

        var request = new MemoryExtractionRequest("user1", stateKey, 6, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        capturedWindow.ShouldNotBeNull();
        capturedWindow.Count.ShouldBe(6);
        capturedWindow[0].Text.ShouldBe("turn1 assistant");
        capturedWindow[^1].Text.ShouldBe("turn4 user");
        capturedWindow.ShouldNotContain(m => m.Text == "turn4 assistant");
        capturedWindow.ShouldNotContain(m => m.Text == "turn5 user (drift)");
    }

    [Fact]
    public async Task ProcessRequestAsync_WithMissingThread_DropsRequestAndPublishesZeroMetric()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("gone"))
            .ReturnsAsync((ChatMessage[]?)null);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        var request = new MemoryExtractionRequest("user1", "gone", 0, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryExtractionEvent>();
        var evt = (MemoryExtractionEvent)published;
        evt.CandidateCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithAnchorBeyondThreadLength_DropsRequest()
    {
        var allMessages = new ChatMessage[]
        {
            new(ChatRole.User, "only message")
        };
        _threadStateStore.Setup(s => s.GetMessagesAsync("short"))
            .ReturnsAsync(allMessages);

        var request = new MemoryExtractionRequest("user1", "short", 99, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithMissingThreadAndFallback_ExtractsFromFallbackContent()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("gone"))
            .ReturnsAsync((ChatMessage[]?)null);

        IReadOnlyList<ChatMessage>? capturedWindow = null;
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>((w, _, _) => capturedWindow = w)
            .ReturnsAsync([]);

        var request = new MemoryExtractionRequest("user1", "gone", 0, "conv_1", null)
        {
            FallbackContent = "I work at Contoso"
        };

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        capturedWindow.ShouldNotBeNull();
        capturedWindow.Count.ShouldBe(1);
        capturedWindow[0].Text.ShouldBe("I work at Contoso");
        capturedWindow[0].Role.ShouldBe(ChatRole.User);
    }
}
