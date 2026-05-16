using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsCollectorServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IHubContext<MetricsHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly MetricsCollectorService _sut;

    public MetricsCollectorServiceTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _hubClients.Setup(c => c.All).Returns(_clientProxy.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _sut = new MetricsCollectorService(
            _redis.Object,
            _hubContext.Object,
            NullLogger<MetricsCollectorService>.Instance);
    }

    [Fact]
    public async Task ProcessEventAsync_TokenUsage_IncrementsDailyTotalsHash()
    {
        var evt = new TokenUsageEvent
        {
            Sender = "alice",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.0025m,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tokens:input", 100, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tokens:output", 50, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tokens:cost", 25, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tokens:byUser:alice", 150, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tokens:byModel:gpt-4", 150, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_TokenUsage_AddsSortedSetEntry()
    {
        var evt = new TokenUsageEvent
        {
            Sender = "alice",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:tokens:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"sender\":\"alice\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_TokenUsage_ForwardsToSignalR()
    {
        var evt = new TokenUsageEvent
        {
            Sender = "alice",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnTokenUsage",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_Heartbeat_UpdatesRedisAndForwardsToSignalR()
    {
        var evt = new HeartbeatEvent
        {
            Service = "agent-1",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "StringSetAsync"
                && i.Arguments[0].ToString() == "metrics:health:agent-1")
            .ShouldBe(1);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnHealthUpdate",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_Error_StoresAndForwardsCorrectly()
    {
        var evt = new ErrorEvent
        {
            Service = "agent",
            ErrorType = "NullRef",
            Message = "Object reference not set",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "ListLeftPushAsync"
                && i.Arguments[0].ToString() == "metrics:errors:recent"
                && i.Arguments[1].ToString()!.Contains("\"errorType\":\"NullRef\""))
            .ShouldBe(1);

        _db.Verify(d => d.ListTrimAsync(
            "metrics:errors:recent", 0, 99, It.IsAny<CommandFlags>()), Times.Once);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:errors:2026-03-15",
            It.IsAny<RedisValue>(),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_IncrementsCountAndByName()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = true,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tools:count", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tools:byName:search", 1, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Failure_IncrementsErrors()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = false,
            Error = "timeout",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tools:errors", 1, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Failure_CreatesErrorEvent()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = false,
            Error = "timeout",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:errors:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"errorType\":\"search\"")
                && v.ToString().Contains("\"message\":\"timeout\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnError",
            It.Is<object[]>(args => args.Length == 1 && args[0] is ErrorEvent),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Success_DoesNotCreateErrorEvent()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = true,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            It.Is<RedisKey>(k => k.ToString().StartsWith("metrics:errors:")),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Success_DoesNotIncrementErrors()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = true,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "tools:errors", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEventAsync_ScheduleExecution_AddsSortedSetEntry()
    {
        var evt = new ScheduleExecutionEvent
        {
            ScheduleId = "sched-1",
            Prompt = "Do something",
            DurationMs = 5000,
            Success = true,
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:schedules:2026-03-15",
            It.IsAny<RedisValue>(),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryRecall_IncrementsDailyTotals()
    {
        var evt = new MemoryRecallEvent
        {
            DurationMs = 250,
            MemoryCount = 5,
            UserId = "alice",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:recalls", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:recallDuration", 250, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:recallMemories", 5, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:byUser:alice", 1, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryRecall_AddsSortedSetEntry()
    {
        var evt = new MemoryRecallEvent
        {
            DurationMs = 250,
            MemoryCount = 5,
            UserId = "alice",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:memory-recall:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"alice\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryRecall_ForwardsToSignalR()
    {
        var evt = new MemoryRecallEvent
        {
            DurationMs = 250,
            MemoryCount = 5,
            UserId = "alice"
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnMemoryRecall",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryExtraction_IncrementsDailyTotals()
    {
        var evt = new MemoryExtractionEvent
        {
            DurationMs = 1500,
            CandidateCount = 8,
            StoredCount = 3,
            UserId = "bob",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:extractions", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:extractionDuration", 1500, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:candidates", 8, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:stored", 3, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:byUser:bob", 1, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryExtraction_AddsSortedSetEntry()
    {
        var evt = new MemoryExtractionEvent
        {
            DurationMs = 1500,
            CandidateCount = 8,
            StoredCount = 3,
            UserId = "bob",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:memory-extraction:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"bob\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryExtraction_ForwardsToSignalR()
    {
        var evt = new MemoryExtractionEvent
        {
            DurationMs = 1500,
            CandidateCount = 8,
            StoredCount = 3,
            UserId = "bob"
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnMemoryExtraction",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryDreaming_IncrementsDailyTotals()
    {
        var evt = new MemoryDreamingEvent
        {
            MergedCount = 7,
            DecayedCount = 3,
            ProfileRegenerated = true,
            UserId = "alice",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:dreamings", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:merged", 7, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:decayed", 3, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:profileRegens", 1, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryDreaming_NoProfileRegen_DoesNotIncrementRegens()
    {
        var evt = new MemoryDreamingEvent
        {
            MergedCount = 4,
            DecayedCount = 1,
            ProfileRegenerated = false,
            UserId = "bob",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "memory:profileRegens", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryDreaming_AddsSortedSetEntry()
    {
        var evt = new MemoryDreamingEvent
        {
            MergedCount = 7,
            DecayedCount = 3,
            ProfileRegenerated = true,
            UserId = "alice",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:memory-dreaming:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"alice\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryDreaming_ForwardsToSignalR()
    {
        var evt = new MemoryDreamingEvent
        {
            MergedCount = 7,
            DecayedCount = 3,
            ProfileRegenerated = true,
            UserId = "alice"
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnMemoryDreaming",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_Latency_IncrementsDailyTotals()
    {
        var evt = new LatencyEvent
        {
            Stage = LatencyStage.LlmTotal,
            DurationMs = 1500,
            Model = "anthropic/claude",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "latency:LlmTotal:count", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "latency:LlmTotal:totalMs", 1500, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_Latency_AddsSortedSetEntry()
    {
        var evt = new LatencyEvent
        {
            Stage = LatencyStage.LlmTotal,
            DurationMs = 1500,
            Model = "anthropic/claude",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:latency:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"type\":\"latency\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_Latency_ForwardsToSignalR()
    {
        var evt = new LatencyEvent { Stage = LatencyStage.MemoryRecall, DurationMs = 42 };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnLatency",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}