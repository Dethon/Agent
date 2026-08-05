using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientMetricsTests : IDisposable
{
    private readonly Mock<IChatClient> _innerClient = new();
    private readonly Mock<IMetricsPublisher> _publisher = new();
    private readonly OpenRouterChatClient _sut;

    public OpenRouterChatClientMetricsTests()
    {
        _sut = new OpenRouterChatClient(_innerClient.Object, "test-model", metricsPublisher: _publisher.Object);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task GetStreamingResponseAsync_WithUsageAndSender_PublishesTokenUsageEvent()
    {
        var userMessage = new ChatMessage(ChatRole.User, "hello");
        userMessage.SetSenderId("alice");

        var usageDetails = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 };
        var usageContent = new UsageContent(usageDetails);

        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] },
            new() { Role = ChatRole.Assistant, Contents = [usageContent] }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        TokenUsageEvent? captured = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => captured = e as TokenUsageEvent);

        await _sut.GetStreamingResponseAsync([userMessage]).ToListAsync();

        captured.ShouldNotBeNull();
        captured.Sender.ShouldBe("alice");
        captured.Model.ShouldBe("test-model");
        captured.InputTokens.ShouldBe(100);
        captured.OutputTokens.ShouldBe(50);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutSender_PublishesWithUnknownSender()
    {
        var userMessage = new ChatMessage(ChatRole.User, "hello");

        var usageDetails = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        await _sut.GetStreamingResponseAsync([userMessage]).ToListAsync();

        _publisher.Verify(
            p => p.Publish(
                It.Is<TokenUsageEvent>(e => e.Sender == "unknown")),
            Times.Once);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutUsage_DoesNotPublish()
    {
        var userMessage = new ChatMessage(ChatRole.User, "hello");
        userMessage.SetSenderId("alice");

        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        await _sut.GetStreamingResponseAsync([userMessage]).ToListAsync();

        _publisher.Verify(
            p => p.Publish(It.IsAny<MetricEvent>()),
            Times.Never);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithNoPublisher_DoesNotThrow()
    {
        using var clientWithoutPublisher = new OpenRouterChatClient(_innerClient.Object, "test-model");

        var userMessage = new ChatMessage(ChatRole.User, "hello");
        userMessage.SetSenderId("alice");

        var usageDetails = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        var result = await clientWithoutPublisher.GetStreamingResponseAsync([userMessage]).ToListAsync();

        result.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UsesSenderFromLastUserMessage()
    {
        var firstMessage = new ChatMessage(ChatRole.User, "first");
        firstMessage.SetSenderId("bob");

        var secondMessage = new ChatMessage(ChatRole.User, "second");
        secondMessage.SetSenderId("alice");

        var usageDetails = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        TokenUsageEvent? captured = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => captured = e as TokenUsageEvent);

        await _sut.GetStreamingResponseAsync([firstMessage, secondMessage]).ToListAsync();

        captured.ShouldNotBeNull();
        captured.Sender.ShouldBe("alice");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithCachedPromptTokens_RecordsThem()
    {
        // Prompt caching is already active on the glm models, but nothing recorded the hit rate, so
        // it could only be inferred from cost against list pricing. This makes it a measurement.
        var usageDetails = new UsageDetails
        {
            InputTokenCount = 21639,
            OutputTokenCount = 50,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["InputTokenDetails.CachedTokenCount"] = 13800
            }
        };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
            }.ToAsyncEnumerable());

        TokenUsageEvent? captured = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => captured = e as TokenUsageEvent ?? captured);

        await _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        captured.ShouldNotBeNull();
        captured.InputTokens.ShouldBe(21639);
        captured.CachedInputTokens.ShouldBe(13800);
    }

    // The model a config patch resolved to arrives on the request's own ChatOptions, so token
    // usage is attributed to the model that produced it without the client re-resolving anything.
    [Fact]
    public async Task GetStreamingResponseAsync_WithModelIdOnOptions_PublishesThatModel()
    {
        var userMessage = new ChatMessage(ChatRole.User, "hello");

        var usageDetails = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 };
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
            }.ToAsyncEnumerable());

        TokenUsageEvent? captured = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => captured = e as TokenUsageEvent ?? captured);

        await _sut.GetStreamingResponseAsync(
            [userMessage], new ChatOptions { ModelId = "patched-model" }).ToListAsync();

        captured.ShouldNotBeNull();
        captured.Model.ShouldBe("patched-model");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutCachedTokenDetail_RecordsNull()
    {
        // A provider reporting no cache detail must read as "unknown", not a confident zero —
        // otherwise a missing field is indistinguishable from a 0% hit rate.
        var usageDetails = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 };

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new UsageContent(usageDetails)] }
            }.ToAsyncEnumerable());

        TokenUsageEvent? captured = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => captured = e as TokenUsageEvent ?? captured);

        await _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        captured.ShouldNotBeNull();
        captured.CachedInputTokens.ShouldBeNull();
    }
}