using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    public MetricsQueryServiceTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_SingleDay_ReturnsSummaryFromHash()
    {
        var date = new DateOnly(2026, 3, 15);
        var hashEntries = new HashEntry[]
        {
            new("tokens:input", 1000),
            new("tokens:output", 500),
            new("tokens:cost", 250),
            new("tools:count", 42),
            new("tools:errors", 3)
        };

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        var result = await _sut.GetSummaryAsync(date, date);

        result.InputTokens.ShouldBe(1000);
        result.OutputTokens.ShouldBe(500);
        result.TotalTokens.ShouldBe(1500);
        result.Cost.ShouldBe(0.025m);
        result.ToolCalls.ShouldBe(42);
        result.ToolErrors.ShouldBe(3);
    }

    [Fact]
    public async Task GetSummaryAsync_MultipleDays_SumsAcrossDays()
    {
        var from = new DateOnly(2026, 3, 14);
        var to = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-14", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 100),
                new HashEntry("tokens:output", 50),
                new HashEntry("tokens:cost", 100),
                new HashEntry("tools:count", 10),
                new HashEntry("tools:errors", 1)
            ]);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 200),
                new HashEntry("tokens:output", 100),
                new HashEntry("tokens:cost", 200),
                new HashEntry("tools:count", 20),
                new HashEntry("tools:errors", 2)
            ]);

        var result = await _sut.GetSummaryAsync(from, to);

        result.InputTokens.ShouldBe(300);
        result.OutputTokens.ShouldBe(150);
        result.TotalTokens.ShouldBe(450);
        result.Cost.ShouldBe(0.03m);
        result.ToolCalls.ShouldBe(30);
        result.ToolErrors.ShouldBe(3);
    }

    [Fact]
    public async Task GetSummaryAsync_NoData_ReturnsZeros()
    {
        var date = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        var result = await _sut.GetSummaryAsync(date, date);

        result.InputTokens.ShouldBe(0);
        result.OutputTokens.ShouldBe(0);
        result.TotalTokens.ShouldBe(0);
        result.Cost.ShouldBe(0m);
        result.ToolCalls.ShouldBe(0);
        result.ToolErrors.ShouldBe(0);
    }

    [Fact]
    public async Task GetSummaryAsync_IgnoresUnrelatedHashFields()
    {
        var date = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 100),
                new HashEntry("tokens:byUser:alice", 100),
                new HashEntry("tokens:byModel:gpt-4", 100),
                new HashEntry("tools:byName:search", 5)
            ]);

        var result = await _sut.GetSummaryAsync(date, date);

        result.InputTokens.ShouldBe(100);
        result.OutputTokens.ShouldBe(0);
        result.ToolCalls.ShouldBe(0);
    }
}
