using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentLatencyTests : IAsyncDisposable
{
    private readonly McpAgent _agent;
    private readonly List<LatencyEvent> _events = [];
    private readonly Lock _lock = new();
    private readonly Mock<IMetricsPublisher> _publisher = new();

    public McpAgentLatencyTests()
    {
        var chatClient = new Mock<IChatClient>();
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] }
        };
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) =>
            {
                if (e is LatencyEvent le)
                {
                    lock (_lock)
                    {
                        _events.Add(le);
                    }
                }
            })
            .Returns(Task.CompletedTask);

        var stateStore = new Mock<IThreadStateStore>();
        _agent = new McpAgent(
            [],
            chatClient.Object,
            "test-agent",
            "",
            stateStore.Object,
            "test-user",
            metricsPublisher: _publisher.Object,
            model: "anthropic/claude",
            conversationId: "conv1");
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
    }

    private LatencyEvent[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _events];
        }
    }

    [Fact]
    public async Task RunStreaming_EmitsLlmFirstTokenAndLlmTotal_WithModel()
    {
        await _agent.RunStreamingAsync("hello").ToListAsync();

        var events = Snapshot();
        var firstToken = events.FirstOrDefault(e => e.Stage == LatencyStage.LlmFirstToken);
        var total = events.FirstOrDefault(e => e.Stage == LatencyStage.LlmTotal);

        firstToken.ShouldNotBeNull();
        total.ShouldNotBeNull();
        total.Model.ShouldBe("anthropic/claude");
        total.ConversationId.ShouldBe("conv1");
        firstToken.Model.ShouldBe("anthropic/claude");
    }

    [Fact]
    public async Task WarmupSessionAsync_EmitsSessionWarmupLatency()
    {
        var thread = await _agent.CreateSessionAsync();

        await _agent.WarmupSessionAsync(thread);

        var warmup = Snapshot().FirstOrDefault(e => e.Stage == LatencyStage.SessionWarmup);
        warmup.ShouldNotBeNull();
        warmup.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        warmup.Model.ShouldBeNull();
    }

    [Fact]
    public async Task PublisherThrowing_DoesNotFailWarmupOrTurn()
    {
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publisher down"));

        await Should.NotThrowAsync(async () =>
        {
            var thread = await _agent.CreateSessionAsync();
            await _agent.WarmupSessionAsync(thread);
            await _agent.RunStreamingAsync("hello", thread).ToListAsync();
        });
    }
}