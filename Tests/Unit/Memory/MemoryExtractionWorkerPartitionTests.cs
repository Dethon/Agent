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

public class MemoryExtractionWorkerPartitionTests
{
    private readonly Mock<IMemoryExtractor> _extractor = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();

    private MemoryExtractionWorker NewWorker(MemoryExtractionQueue queue, MemoryExtractionOptions options) =>
        new(queue, _extractor.Object, _embeddingService.Object, _store.Object,
            _threadStateStore.Object, _metricsPublisher.Object, _agentDefinitionProvider.Object,
            NullLogger<MemoryExtractionWorker>.Instance, options);

    private static MemoryExtractionRequest Req(string userId, string content) =>
        new(userId, ThreadStateKey: null, AnchorIndex: 0, ConversationId: null, AgentId: null)
        {
            FallbackContent = content
        };

    [Fact]
    public async Task DifferentUsers_OnDifferentLanes_ProcessedConcurrently()
    {
        const int laneCount = 4;
        var options = new MemoryExtractionOptions { LaneCount = laneCount };
        var userA = "userA";
        var userB = Enumerable.Range(0, 100).Select(i => $"user-{i}")
            .First(u => MemoryLaneRouter.LaneFor(u, laneCount) != MemoryLaneRouter.LaneFor(userA, laneCount));

        var gateA = new TaskCompletionSource();
        var bProcessed = new TaskCompletionSource();

        _extractor.Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w => w.Any(m => m.Text == "blockA")),
                userA, It.IsAny<CancellationToken>()))
            .Returns(async () => { await gateA.Task; return []; });
        _extractor.Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w => w.Any(m => m.Text == "fastB")),
                userB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { bProcessed.TrySetResult(); return []; });

        var queue = new MemoryExtractionQueue();
        var worker = NewWorker(queue, options);
        await worker.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(Req(userA, "blockA"), CancellationToken.None);
        await queue.EnqueueAsync(Req(userB, "fastB"), CancellationToken.None);

        var winner = await Task.WhenAny(bProcessed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        winner.ShouldBe(bProcessed.Task, "userB must process while userA is blocked on another lane");

        gateA.SetResult();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SameUser_RequestsProcessedSequentially()
    {
        var options = new MemoryExtractionOptions { LaneCount = 4 };
        var active = 0;
        var maxConcurrent = 0;
        var order = new List<string>();
        var bothSeen = new TaskCompletionSource();
        var lockObj = new object();

        _extractor.Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(), "solo", It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<ChatMessage> w, string _, CancellationToken _) =>
            {
                var now = Interlocked.Increment(ref active);
                lock (lockObj)
                {
                    maxConcurrent = Math.Max(maxConcurrent, now);
                    order.Add(w[0].Text);
                    if (order.Count == 2)
                    {
                        bothSeen.TrySetResult();
                    }
                }

                await Task.Delay(100);
                Interlocked.Decrement(ref active);
                return [];
            });

        var queue = new MemoryExtractionQueue();
        var worker = NewWorker(queue, options);
        await worker.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(Req("solo", "m1"), CancellationToken.None);
        await queue.EnqueueAsync(Req("solo", "m2"), CancellationToken.None);

        var winner = await Task.WhenAny(bothSeen.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        winner.ShouldBe(bothSeen.Task);
        await worker.StopAsync(CancellationToken.None);

        maxConcurrent.ShouldBe(1, "same user must be processed one at a time");
        order.ShouldBe(["m1", "m2"]);
    }
}