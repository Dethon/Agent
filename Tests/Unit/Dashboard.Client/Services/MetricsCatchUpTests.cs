using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Domain.DTOs.Metrics.Enums;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Services;

public sealed class MetricsCatchUpTests : IDisposable
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 2);

    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricFamilyTable _families;
    private readonly MetricsCatchUp _catchUp;

    public MetricsCatchUpTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _families = new MetricFamilyTable(
            new MetricsApiService(http), _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        _catchUp = new MetricsCatchUp(_families);

        _tokensStore.SetDateRange(From, To);
        _toolsStore.SetDateRange(From, To);
        _errorsStore.SetDateRange(From, To);
        _schedulesStore.SetDateRange(From, To);
        _memoryStore.SetDateRange(From, To);
        _latencyStore.SetDateRange(From, To);
        _voiceStore.SetDateRange(From, To);
    }

    public void Dispose()
    {
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
    }

    // Catch-up reloads every family whatever any one of them answers, and reports the failure to
    // its caller — the live connection, which logs it and stays live. Nothing here stages every
    // response, so these drive it through that same failure.
    private async Task CatchUpAsync()
    {
        try
        {
            await _catchUp.CatchUpAsync();
        }
        catch (HttpRequestException)
        {
            // A family with nothing staged answers 404 and keeps its last known values.
        }
    }

    // Every family, events and breakdown, over the range the family already holds. The assertion is
    // on what went out.
    public static TheoryData<string, string, string> FamilyRequests => new()
    {
        { "tokens", "api/metrics/tokens?", "api/metrics/tokens/by/User?metric=Tokens&" },
        { "tools", "api/metrics/tools?", "api/metrics/tools/by/ToolName?metric=CallCount&" },
        { "errors", "api/metrics/errors/range?", "api/metrics/errors/by/Service?" },
        { "schedules", "api/metrics/schedules?", "api/metrics/schedules/by/Schedule?" },
        { "memory", "api/metrics/memory/recall?", "api/metrics/memory/by/User?metric=Count&" },
        { "latency", "api/metrics/latency?", "api/metrics/latency/by/Stage?metric=P95&" },
        { "voice", "api/metrics/voice?", "api/metrics/voice/by/SatelliteId?metric=UtteranceTranscribed&agg=Avg&" },
    };

    [Theory]
    [MemberData(nameof(FamilyRequests))]
    public async Task CatchUpAsync_AnyFamily_ReloadsItForTheRangeTheFamilyHolds(
        string _, string eventsRequest, string breakdownRequest)
    {
        var range = "from=2026-03-01&to=2026-03-02";

        await CatchUpAsync();

        _handler.Requests.ShouldContain(u => u != null && u.Contains(eventsRequest + range, StringComparison.Ordinal));
        _handler.Requests.ShouldContain(u => u != null && u.Contains(breakdownRequest + range, StringComparison.Ordinal));
    }

    // Recovering must not move the page under whoever is reading it.
    [Fact]
    public async Task CatchUpAsync_TheUserHasChosenGroupByMetricAndTime_LeavesEveryChoiceAlone()
    {
        _voiceStore.SetGroupBy(VoiceDimension.Identity);
        _voiceStore.SetMetric(VoiceMetric.SttLatencyMs);
        _voiceStore.SetAgg(Aggregation.P95);
        _tokensStore.SetGroupBy(TokenDimension.Model);
        _tokensStore.SetMetric(TokenMetric.Cost);

        await CatchUpAsync();

        _voiceStore.State.GroupBy.ShouldBe(VoiceDimension.Identity);
        _voiceStore.State.Metric.ShouldBe(VoiceMetric.SttLatencyMs);
        _voiceStore.State.Agg.ShouldBe(Aggregation.P95);
        _tokensStore.State.GroupBy.ShouldBe(TokenDimension.Model);
        _tokensStore.State.Metric.ShouldBe(TokenMetric.Cost);
        _tokensStore.State.From.ShouldBe(From);
        _tokensStore.State.To.ShouldBe(To);
    }

    [Fact]
    public async Task CatchUpAsync_EventsArrivedDuringTheOutage_WritesThemToTheStore()
    {
        _handler.AnswerFor("api/metrics/voice?", new List<VoiceEventPayload>
        {
            new((int)VoiceMetric.UtteranceTranscribed, "kitchen-01"),
        });

        await CatchUpAsync();

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    private sealed record VoiceEventPayload(int Metric, string SatelliteId);
}