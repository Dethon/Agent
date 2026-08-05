using Dashboard.Client.Effects;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Effects;

public sealed class DataLoadEffectTests : IDisposable
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 2);

    private static readonly MetricsSummary Summary = new(
        InputTokens: 120, OutputTokens: 30, TotalTokens: 150, Cost: 1.5m, ToolCalls: 4, ToolErrors: 1);

    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly MetricFamilyTable _families;
    private readonly DataLoadEffect _dataLoad;

    public DataLoadEffectTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var api = new MetricsApiService(http);
        _families = new MetricFamilyTable(
            api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        _dataLoad = new DataLoadEffect(
            _families, new OverviewFigures(api, _metricsStore, _healthStore));
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
        _metricsStore.Dispose();
        _healthStore.Dispose();
    }

    // One page load is around nineteen requests. Only the two the Overview KPI row and the Service
    // Health grid are drawn from are staged here; every breakdown endpoint answers 404. Loading them
    // as one all-or-nothing batch is what used to blank both panels because a breakdown failed.
    [Fact]
    public async Task LoadAsync_SomeRequestsFail_TheOnesThatAnsweredStillReachTheirStores()
    {
        StageSummaryAndHealth();

        await _dataLoad.LoadAsync(From, To);

        _metricsStore.State.InputTokens.ShouldBe(120);
        _metricsStore.State.Cost.ShouldBe(1.5m);
        _healthStore.State.Services.ShouldContain(s => s.Service == "agent" && s.IsHealthy);
    }

    // Isolating the failures does not hide them: the recorded failure is what makes the live
    // connection catch up on its first epoch instead of trusting the load.
    [Fact]
    public async Task LoadAsync_SomeRequestsFail_StillRecordsTheFailure()
    {
        StageSummaryAndHealth();

        await _dataLoad.LoadAsync(From, To);

        _dataLoad.LastLoadFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_EveryRequestAnswers_RecordsNoFailure()
    {
        StageEveryRequest();

        await _dataLoad.LoadAsync(From, To);

        _dataLoad.LastLoadFailed.ShouldBeFalse();
    }

    // A live push refreshes the same breakdowns a page load asks for, and MetricFamily shares one
    // run between them. A load that arrives on top of a refresh that is about to fail is handed that
    // failed run, so per-request isolation has to cover a task the load never started.
    [Fact]
    public async Task LoadAsync_ASharedRefreshRunIsAlreadyFailing_TheOtherRequestsStillApply()
    {
        StageSummaryAndHealth();
        var failing = _families.Tokens.RefreshAsync();

        await _dataLoad.LoadAsync(From, To);
        await Should.ThrowAsync<HttpRequestException>(() => failing);

        _metricsStore.State.InputTokens.ShouldBe(120);
        _healthStore.State.Services.ShouldContain(s => s.Service == "agent");
    }

    private void StageSummaryAndHealth()
    {
        _handler.AnswerFor("api/metrics/summary", Summary);
        _handler.AnswerFor("api/metrics/health", new List<ServiceHealthResponse>
        {
            new("agent", true, "2026-03-02T10:00:00Z"),
        });
    }

    // Every endpoint a page load reaches for, answered with an empty body where the shape allows it.
    // The fragments are disjoint, so each request matches exactly one of them.
    private void StageEveryRequest()
    {
        StageSummaryAndHealth();

        new[]
        {
            "api/metrics/tokens?", "api/metrics/tools?", "api/metrics/errors/range?",
            "api/metrics/schedules?", "api/metrics/memory/recall?", "api/metrics/memory/extraction?",
            "api/metrics/memory/dreaming?", "api/metrics/latency?", "api/metrics/latency/trend?",
            "api/metrics/voice?",
        }.ToList().ForEach(fragment => _handler.AnswerFor(fragment, Array.Empty<object>()));

        new[]
        {
            "tokens/by/", "tools/by/", "errors/by/", "schedules/by/",
            "memory/by/", "latency/by/", "voice/by/",
        }.ToList().ForEach(fragment => _handler.AnswerFor(fragment, new Dictionary<string, decimal>()));
    }
}