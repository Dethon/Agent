using Domain.DTOs.Metrics;
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
}