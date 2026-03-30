using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceGroupingTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetricsQueryServiceGroupingTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private void SetupSortedSet(string key, IEnumerable<MetricEvent> events)
    {
        var entries = events
            .Select(e => new RedisValue(JsonSerializer.Serialize(e, _jsonOptions)))
            .ToArray();
        _db.Setup(d => d.SortedSetRangeByScoreAsync(key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    // =====================================================================
    // Task 3: Token Grouped Aggregation
    // =====================================================================

    [Fact]
    public async Task GetTokenGroupedAsync_ByUser_Tokens_SumsTokensPerSender()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
        [
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.1m },
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.2m },
            new TokenUsageEvent { Sender = "bob", Model = "gpt-4", InputTokens = 50, OutputTokens = 25, Cost = 0.05m }
        ]);

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.User, TokenMetric.Tokens, date, date);

        result["alice"].ShouldBe(450m); // (100+50) + (200+100)
        result["bob"].ShouldBe(75m);    // 50+25
    }

    [Fact]
    public async Task GetTokenGroupedAsync_ByModel_Cost_SumsCostPerModel()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
        [
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.1m },
            new TokenUsageEvent { Sender = "bob", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.3m },
            new TokenUsageEvent { Sender = "alice", Model = "claude-3", InputTokens = 50, OutputTokens = 25, Cost = 0.05m }
        ]);

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.Model, TokenMetric.Cost, date, date);

        result["gpt-4"].ShouldBe(0.4m);
        result["claude-3"].ShouldBe(0.05m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_ByAgent_Tokens_GroupsByAgentId()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
        [
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.1m, AgentId = "agent-1" },
            new TokenUsageEvent { Sender = "bob", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.3m, AgentId = "agent-1" },
            new TokenUsageEvent { Sender = "carol", Model = "gpt-4", InputTokens = 50, OutputTokens = 25, Cost = 0.05m, AgentId = null }
        ]);

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.Agent, TokenMetric.Tokens, date, date);

        result["agent-1"].ShouldBe(450m); // (100+50) + (200+100)
        result["unknown"].ShouldBe(75m);  // 50+25
    }

    [Fact]
    public async Task GetTokenGroupedAsync_EmptyData_ReturnsEmptyDictionary()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15", []);

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.User, TokenMetric.Tokens, date, date);

        result.ShouldBeEmpty();
    }

    // =====================================================================
    // Task 4: Tools Grouped Aggregation
    // =====================================================================

    [Fact]
    public async Task GetToolGroupedAsync_ByTool_CallCount_CountsPerTool()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tools:2026-03-15",
        [
            new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
            new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = true },
            new ToolCallEvent { ToolName = "read_file", DurationMs = 50, Success = false }
        ]);

        var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.CallCount, date, date);

        result["search"].ShouldBe(2m);
        result["read_file"].ShouldBe(1m);
    }

    [Fact]
    public async Task GetToolGroupedAsync_ByTool_AvgDuration_AveragesPerTool()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tools:2026-03-15",
        [
            new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
            new ToolCallEvent { ToolName = "search", DurationMs = 300, Success = true },
            new ToolCallEvent { ToolName = "read_file", DurationMs = 50, Success = true }
        ]);

        var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.AvgDuration, date, date);

        result["search"].ShouldBe(200m);  // (100+300)/2
        result["read_file"].ShouldBe(50m);
    }

    [Fact]
    public async Task GetToolGroupedAsync_ByTool_ErrorRate_CalculatesPercentage()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tools:2026-03-15",
        [
            new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
            new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = false },
            new ToolCallEvent { ToolName = "search", DurationMs = 150, Success = false },
            new ToolCallEvent { ToolName = "read_file", DurationMs = 50, Success = true }
        ]);

        var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.ErrorRate, date, date);

        // search: 2 failures out of 3 = 66.666...%
        result["search"].ShouldBeInRange(66.66m, 66.67m);
        result["read_file"].ShouldBe(0m);
    }

    [Fact]
    public async Task GetToolGroupedAsync_ByStatus_CallCount_CountsPerStatus()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tools:2026-03-15",
        [
            new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
            new ToolCallEvent { ToolName = "read_file", DurationMs = 50, Success = false },
            new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = true }
        ]);

        var result = await _sut.GetToolGroupedAsync(ToolDimension.Status, ToolMetric.CallCount, date, date);

        result["Success"].ShouldBe(2m);
        result["Failure"].ShouldBe(1m);
    }

    // =====================================================================
    // Task 5: Errors & Schedules Grouped Aggregation
    // =====================================================================

    [Fact]
    public async Task GetErrorGroupedAsync_ByService_CountsPerService()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:errors:2026-03-15",
        [
            new ErrorEvent { Service = "Agent", ErrorType = "NullReference", Message = "oops" },
            new ErrorEvent { Service = "Agent", ErrorType = "Timeout", Message = "timed out" },
            new ErrorEvent { Service = "Observability", ErrorType = "NullReference", Message = "oops2" }
        ]);

        var result = await _sut.GetErrorGroupedAsync(ErrorDimension.Service, date, date);

        result["Agent"].ShouldBe(2);
        result["Observability"].ShouldBe(1);
    }

    [Fact]
    public async Task GetErrorGroupedAsync_ByErrorType_CountsPerType()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:errors:2026-03-15",
        [
            new ErrorEvent { Service = "Agent", ErrorType = "NullReference", Message = "oops" },
            new ErrorEvent { Service = "Observability", ErrorType = "NullReference", Message = "oops2" },
            new ErrorEvent { Service = "Agent", ErrorType = "Timeout", Message = "timed out" }
        ]);

        var result = await _sut.GetErrorGroupedAsync(ErrorDimension.ErrorType, date, date);

        result["NullReference"].ShouldBe(2);
        result["Timeout"].ShouldBe(1);
    }

    [Fact]
    public async Task GetScheduleGroupedAsync_BySchedule_CountsPerScheduleId()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:schedules:2026-03-15",
        [
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1000, Success = true },
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1200, Success = false },
            new ScheduleExecutionEvent { ScheduleId = "weekly-summary", Prompt = "...", DurationMs = 2000, Success = true }
        ]);

        var result = await _sut.GetScheduleGroupedAsync(ScheduleDimension.Schedule, date, date);

        result["daily-report"].ShouldBe(2);
        result["weekly-summary"].ShouldBe(1);
    }

    [Fact]
    public async Task GetScheduleGroupedAsync_ByStatus_CountsPerStatus()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:schedules:2026-03-15",
        [
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1000, Success = true },
            new ScheduleExecutionEvent { ScheduleId = "daily-report", Prompt = "...", DurationMs = 1200, Success = false },
            new ScheduleExecutionEvent { ScheduleId = "weekly-summary", Prompt = "...", DurationMs = 2000, Success = true }
        ]);

        var result = await _sut.GetScheduleGroupedAsync(ScheduleDimension.Status, date, date);

        result["Success"].ShouldBe(2);
        result["Failure"].ShouldBe(1);
    }

    // =====================================================================
    // Memory Grouped Aggregation
    // =====================================================================

    [Fact]
    public async Task GetMemoryGroupedAsync_ByUser_Count_CountsPerUser()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15",
        [
            new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
            new MemoryRecallEvent { DurationMs = 200, MemoryCount = 3, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-extraction:2026-03-15",
        [
            new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "bob" },
        ]);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15",
        [
            new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice" },
        ]);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.Count, date, date);

        result["alice"].ShouldBe(3m); // 2 recalls + 1 dreaming
        result["bob"].ShouldBe(1m);   // 1 extraction
    }

    [Fact]
    public async Task GetMemoryGroupedAsync_ByEventType_Count_CountsPerType()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15",
        [
            new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
            new MemoryRecallEvent { DurationMs = 200, MemoryCount = 3, UserId = "bob" },
        ]);
        SetupSortedSet("metrics:memory-extraction:2026-03-15",
        [
            new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.EventType, MemoryMetric.Count, date, date);

        result["Recall"].ShouldBe(2m);
        result["Extraction"].ShouldBe(1m);
    }

    [Fact]
    public async Task GetMemoryGroupedAsync_ByUser_AvgDuration_AveragesRecallAndExtractionOnly()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15",
        [
            new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
            new MemoryRecallEvent { DurationMs = 300, MemoryCount = 3, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-extraction:2026-03-15",
        [
            new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15",
        [
            new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice" },
        ]);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.AvgDuration, date, date);

        // Only recall + extraction have duration: (100 + 300 + 1000) / 3 = 466.666...
        result["alice"].ShouldBeInRange(466.66m, 466.67m);
    }

    [Fact]
    public async Task GetMemoryGroupedAsync_ByEventType_StoredCount_SumsFromExtractions()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15",
        [
            new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
        ]);
        SetupSortedSet("metrics:memory-extraction:2026-03-15",
        [
            new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
            new MemoryExtractionEvent { DurationMs = 2000, CandidateCount = 12, StoredCount = 5, UserId = "bob" },
        ]);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.EventType, MemoryMetric.StoredCount, date, date);

        result["Extraction"].ShouldBe(8m); // 3 + 5
        result["Recall"].ShouldBe(0m);     // recalls have no StoredCount
    }

    [Fact]
    public async Task GetMemoryGroupedAsync_ByAgent_MergedCount_SumsFromDreaming()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15", []);
        SetupSortedSet("metrics:memory-extraction:2026-03-15", []);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15",
        [
            new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice", AgentId = "agent-1" },
            new MemoryDreamingEvent { MergedCount = 3, DecayedCount = 1, ProfileRegenerated = false, UserId = "bob", AgentId = "agent-1" },
            new MemoryDreamingEvent { MergedCount = 7, DecayedCount = 4, ProfileRegenerated = true, UserId = "alice", AgentId = null },
        ]);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.Agent, MemoryMetric.MergedCount, date, date);

        result["agent-1"].ShouldBe(8m); // 5 + 3
        result["unknown"].ShouldBe(7m);
    }

    [Fact]
    public async Task GetMemoryGroupedAsync_EmptyData_ReturnsEmptyDictionary()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:memory-recall:2026-03-15", []);
        SetupSortedSet("metrics:memory-extraction:2026-03-15", []);
        SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

        var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.Count, date, date);

        result.ShouldBeEmpty();
    }
}