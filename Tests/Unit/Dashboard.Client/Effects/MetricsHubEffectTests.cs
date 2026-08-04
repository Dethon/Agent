using System.Collections.Concurrent;
using Dashboard.Client.Effects;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Effects;

public class MetricsHubEffectTests : IAsyncDisposable
{
    private readonly FakeMetricsHub _hub = new();
    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly ConnectionStore _connectionStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricsApiService _api;
    private readonly MetricFamilyTable _families;
    private readonly MetricsHubEffect _effect;

    public MetricsHubEffectTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _api = new MetricsApiService(http);
        _families = new MetricFamilyTable(
            _api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        _effect = new MetricsHubEffect(
            _hub, _families, _metricsStore, _healthStore, _connectionStore);
    }

    public async ValueTask DisposeAsync()
    {
        await _effect.DisposeAsync();
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _metricsStore.Dispose();
        _healthStore.Dispose();
        _connectionStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
    }

    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 2);

    // Every family, in the general form of the aggregation bug: whatever the store holds is what
    // goes out, and whatever comes back is what the family charts.
    private static readonly Dictionary<
        string,
        (Func<MetricFamilyTable, MetricFamily> Family,
         Action<MetricsHubEffectTests> Choose,
         string ExpectedRequest,
         object Breakdown,
         Func<MetricsHubEffectTests, object?> GetBreakdown,
         object? SecondResponse)>
    _familyCases = new()
    {
        ["tokens"] = (
            t => t.Tokens,
            self =>
            {
                self._tokensStore.SetGroupBy(TokenDimension.Model);
                self._tokensStore.SetMetric(TokenMetric.Cost);
            },
            "api/metrics/tokens/by/Model?metric=Cost&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["gpt"] = 12m },
            self => self._tokensStore.State.Breakdown,
            null),
        ["tools"] = (
            t => t.Tools,
            self =>
            {
                self._toolsStore.SetGroupBy(ToolDimension.Status);
                self._toolsStore.SetMetric(ToolMetric.AvgDuration);
            },
            "api/metrics/tools/by/Status?metric=AvgDuration&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["ok"] = 30m },
            self => self._toolsStore.State.Breakdown,
            null),
        ["errors"] = (
            t => t.Errors,
            self => self._errorsStore.SetGroupBy(ErrorDimension.ErrorType),
            "api/metrics/errors/by/ErrorType?from=2026-03-01&to=2026-03-02",
            new Dictionary<string, int> { ["timeout"] = 4 },
            self => self._errorsStore.State.Breakdown,
            null),
        ["schedules"] = (
            t => t.Schedules,
            self => self._schedulesStore.SetGroupBy(ScheduleDimension.Status),
            "api/metrics/schedules/by/Status?from=2026-03-01&to=2026-03-02",
            new Dictionary<string, int> { ["ok"] = 9 },
            self => self._schedulesStore.State.Breakdown,
            null),
        ["memory"] = (
            t => t.Memory,
            self =>
            {
                self._memoryStore.SetGroupBy(MemoryDimension.Agent);
                self._memoryStore.SetMetric(MemoryMetric.AvgDuration);
            },
            "api/metrics/memory/by/Agent?metric=AvgDuration&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["nabu"] = 80m },
            self => self._memoryStore.State.Breakdown,
            null),
        ["latency"] = (
            t => t.Latency,
            self =>
            {
                self._latencyStore.SetGroupBy(LatencyDimension.Model);
                self._latencyStore.SetMetric(Aggregation.P99);
            },
            "api/metrics/latency/by/Model?metric=P99&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["m1"] = 900m },
            self => self._latencyStore.State.Breakdown,
            new List<LatencyTrendSeries>()),
        ["voice"] = (
            t => t.Voice,
            self =>
            {
                self._voiceStore.SetGroupBy(VoiceDimension.Identity);
                self._voiceStore.SetMetric(VoiceMetric.SttLatencyMs);
                self._voiceStore.SetAgg(Aggregation.P95);
            },
            "api/metrics/voice/by/Identity?metric=SttLatencyMs&agg=P95&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["frank"] = 210m },
            self => self._voiceStore.State.Breakdown,
            null),
    };

    public static TheoryData<string> FamilyNames => new(_familyCases.Keys);

    [Theory]
    [MemberData(nameof(FamilyNames))]
    public async Task RefreshAsync_AnyFamily_SendsEveryChoiceInItsStoreAndWritesTheBreakdown(string name)
    {
        var (family, choose, expectedRequest, breakdown, getBreakdown, secondResponse) = _familyCases[name];
        SetDateRangeOnEveryStore();
        choose(this);
        _handler.EnqueueResponse(breakdown, delay: TimeSpan.Zero);
        if (secondResponse is not null)
        {
            _handler.EnqueueResponse(secondResponse, delay: TimeSpan.Zero);
        }

        await family(_families).RefreshAsync();

        _handler.Requests.ShouldContain(u => u != null && u.EndsWith(expectedRequest, StringComparison.Ordinal));
        getBreakdown(this).ShouldBe(breakdown);
    }

    private void SetDateRangeOnEveryStore()
    {
        _tokensStore.SetDateRange(From, To);
        _toolsStore.SetDateRange(From, To);
        _errorsStore.SetDateRange(From, To);
        _schedulesStore.SetDateRange(From, To);
        _memoryStore.SetDateRange(From, To);
        _latencyStore.SetDateRange(From, To);
        _voiceStore.SetDateRange(From, To);
    }

    private static readonly
    Dictionary<
        string,
        (object StaleData, object FreshData, Func<FakeMetricsHub, Task> FireEvent, Func<MetricsHubEffectTests, object?> GetBreakdown)>
    _rapidEventCases = new()
    {
        ["TokenUsage"] = (
            new Dictionary<string, decimal> { ["stale-model"] = 100m },
            new Dictionary<string, decimal> { ["fresh-model"] = 200m },
            hub => hub.FireTokenUsage(new TokenUsageEvent
            { Sender = "test", Model = "m", InputTokens = 1, OutputTokens = 1, Cost = 0.01m }),
            self => self._tokensStore.State.Breakdown),
        ["ToolCall"] = (
            new Dictionary<string, decimal> { ["stale-tool"] = 10m },
            new Dictionary<string, decimal> { ["fresh-tool"] = 20m },
            hub => hub.FireToolCall(new ToolCallEvent
            { ToolName = "t", Success = true, DurationMs = 100 }),
            self => self._toolsStore.State.Breakdown),
        ["Error"] = (
            new Dictionary<string, int> { ["stale-err"] = 5 },
            new Dictionary<string, int> { ["fresh-err"] = 10 },
            hub => hub.FireError(new ErrorEvent
            { Message = "err", Service = "s", ErrorType = "e" }),
            self => self._errorsStore.State.Breakdown),
        ["ScheduleExecution"] = (
            new Dictionary<string, int> { ["stale-sched"] = 3 },
            new Dictionary<string, int> { ["fresh-sched"] = 7 },
            hub => hub.FireScheduleExecution(new ScheduleExecutionEvent
            { ScheduleId = "s", Prompt = "p", Success = true, DurationMs = 50 }),
            self => self._schedulesStore.State.Breakdown),
        ["MemoryRecall"] = (
            new Dictionary<string, decimal> { ["stale-memory"] = 50m },
            new Dictionary<string, decimal> { ["fresh-memory"] = 100m },
            hub => hub.FireMemoryRecall(new MemoryRecallEvent
            { DurationMs = 100, MemoryCount = 5, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
        ["MemoryExtraction"] = (
            new Dictionary<string, decimal> { ["stale-extract"] = 30m },
            new Dictionary<string, decimal> { ["fresh-extract"] = 60m },
            hub => hub.FireMemoryExtraction(new MemoryExtractionEvent
            { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
        ["MemoryDreaming"] = (
            new Dictionary<string, decimal> { ["stale-dream"] = 10m },
            new Dictionary<string, decimal> { ["fresh-dream"] = 20m },
            hub => hub.FireMemoryDreaming(new MemoryDreamingEvent
            { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
    };

    public static TheoryData<string> RapidEventCaseNames => new(_rapidEventCases.Keys);

    // Five events arriving during one outstanding response cost the observability service two
    // aggregations, not five: the run in flight is shared and repeats once for the state that moved
    // under it.
    [Theory]
    [MemberData(nameof(RapidEventCaseNames))]
    public async Task RapidEvents_CoalesceIntoTwoRequestsEndingAtTheLastValue(string caseName)
    {
        var (staleData, freshData, fireEvent, getBreakdown) = _rapidEventCases[caseName];

        await _effect.StartAsync();

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var fires = Enumerable.Range(0, 5).Select(_ => fireEvent(_hub)).ToArray();
        await Task.WhenAll(fires);

        _handler.Requests.Count.ShouldBe(2);
        getBreakdown(this).ShouldBe(freshData);
    }

    [Fact]
    public async Task LiveEvent_RefreshFails_LeavesTheBreakdownAtItsLastKnownValue()
    {
        var lastKnown = new Dictionary<string, decimal> { ["kept"] = 42m };
        _tokensStore.SetBreakdown(lastKnown);
        await _effect.StartAsync();

        // Nothing is staged, so the handler answers 404 and the refresh throws.
        await _hub.FireTokenUsage(new TokenUsageEvent
        { Sender = "test", Model = "m", InputTokens = 1, OutputTokens = 1, Cost = 0.01m });

        _tokensStore.State.Breakdown.ShouldBe(lastKnown);
    }

    [Fact]
    public async Task RefreshAsync_Failing_ThrowsToItsCaller()
    {
        await Should.ThrowAsync<HttpRequestException>(() => _families.Tokens.RefreshAsync());
    }

    [Fact]
    public async Task OnLatency_AppendsEventToLatencyStore()
    {
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _handler.EnqueueResponse(new List<LatencyTrendSeries>(), delay: TimeSpan.Zero);
        await _effect.StartAsync();

        await _hub.FireLatency(new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 5 });

        _latencyStore.State.Events.ShouldContain(e => e.Stage == LatencyStage.LlmTotal);
    }

    [Fact]
    public async Task OnVoice_AppendsEventToVoiceStore()
    {
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        await _effect.StartAsync();

        await _hub.FireVoice(new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01" });

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    [Fact]
    public async Task OnVoice_RequestsBreakdownUsingStoreAgg()
    {
        // Regression guard for the P95-pill-but-Avg-chart bug: a live voice event must refetch
        // the breakdown using whatever aggregation the user picked, not silently fall back to Avg.
        _voiceStore.SetAgg(Aggregation.P95);
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        await _effect.StartAsync();

        await _hub.FireVoice(new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01" });

        _handler.LastRequestUri.ShouldNotBeNull();
        _handler.LastRequestUri!.ShouldContain("agg=P95");
    }

    public static TheoryData<string, Func<IDisposable>, Action<object, DateOnly, DateOnly>, Func<object, DateOnly>, Func<object, DateOnly>> StoreFactories =>
        new()
        {
            { "Errors", () => new ErrorsStore(), (s, f, t) => ((ErrorsStore)s).SetDateRange(f, t), s => ((ErrorsStore)s).State.From, s => ((ErrorsStore)s).State.To },
            { "Schedules", () => new SchedulesStore(), (s, f, t) => ((SchedulesStore)s).SetDateRange(f, t), s => ((SchedulesStore)s).State.From, s => ((SchedulesStore)s).State.To },
            { "Tokens", () => new TokensStore(), (s, f, t) => ((TokensStore)s).SetDateRange(f, t), s => ((TokensStore)s).State.From, s => ((TokensStore)s).State.To },
            { "Tools", () => new ToolsStore(), (s, f, t) => ((ToolsStore)s).SetDateRange(f, t), s => ((ToolsStore)s).State.From, s => ((ToolsStore)s).State.To },
            { "Latency", () => new LatencyStore(), (s, f, t) => ((LatencyStore)s).SetDateRange(f, t), s => ((LatencyStore)s).State.From, s => ((LatencyStore)s).State.To },
            { "Voice", () => new VoiceStore(), (s, f, t) => ((VoiceStore)s).SetDateRange(f, t), s => ((VoiceStore)s).State.From, s => ((VoiceStore)s).State.To },
        };

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void SetDateRange_UpdatesFromAndTo(
        string _,
        Func<IDisposable> factory,
        Action<object, DateOnly, DateOnly> setDateRange,
        Func<object, DateOnly> getFrom,
        Func<object, DateOnly> getTo)
    {
        using var store = factory();
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        setDateRange(store, from, to);

        getFrom(store).ShouldBe(from);
        getTo(store).ShouldBe(to);
    }

    [Fact]
    public void VoiceStore_SetAgg_UpdatesState()
    {
        using var store = new VoiceStore();

        store.SetAgg(Aggregation.P95);

        store.State.Agg.ShouldBe(Aggregation.P95);
    }

    [Fact]
    public async Task LoadAsync_RequestsVoiceBreakdownUsingStoreAgg()
    {
        // DataLoadEffect is a third, independent call site for GetVoiceGroupedAsync (the page-load
        // path, distinct from MetricsHubEffect's live-refresh path) that can silently omit `agg`
        // and fall back to Avg. No response staging is needed: we only assert on the outbound
        // request, and DataLoadEffect swallows the resulting 404s from the unstaffed FakeApiHandler.
        _voiceStore.SetAgg(Aggregation.P95);
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var dataLoadEffect = new DataLoadEffect(
            new MetricsApiService(http), _metricsStore, _healthStore, _tokensStore, _toolsStore,
            _errorsStore, _schedulesStore, _connectionStore, _memoryStore, _latencyStore, _voiceStore);

        await dataLoadEffect.LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 24));

        _handler.Requests.ShouldContain(u => u != null && u.Contains("voice/by") && u.Contains("agg=P95"));
    }
}

public sealed class FakeMetricsHub : MetricsHubService
{
    private readonly List<Func<TokenUsageEvent, Task>> _tokenHandlers = [];
    private readonly List<Func<ToolCallEvent, Task>> _toolHandlers = [];
    private readonly List<Func<ErrorEvent, Task>> _errorHandlers = [];
    private readonly List<Func<ScheduleExecutionEvent, Task>> _scheduleHandlers = [];
    // ReSharper disable once CollectionNeverQueried.Local
    private readonly List<Func<ServiceHealthUpdate, Task>> _healthHandlers = [];
    private readonly List<Func<MemoryRecallEvent, Task>> _recallHandlers = [];
    private readonly List<Func<MemoryExtractionEvent, Task>> _extractionHandlers = [];
    private readonly List<Func<MemoryDreamingEvent, Task>> _dreamingHandlers = [];
    private readonly List<Func<ContextTruncationEvent, Task>> _truncationHandlers = [];
    private readonly List<Func<LatencyEvent, Task>> _latencyHandlers = [];
    private readonly List<Func<VoiceEvent, Task>> _voiceHandlers = [];

    public override IDisposable OnTokenUsage(Func<TokenUsageEvent, Task> handler)
    {
        _tokenHandlers.Add(handler);
        return new ActionDisposable(() => _tokenHandlers.Remove(handler));
    }

    public override IDisposable OnToolCall(Func<ToolCallEvent, Task> handler)
    {
        _toolHandlers.Add(handler);
        return new ActionDisposable(() => _toolHandlers.Remove(handler));
    }

    public override IDisposable OnError(Func<ErrorEvent, Task> handler)
    {
        _errorHandlers.Add(handler);
        return new ActionDisposable(() => _errorHandlers.Remove(handler));
    }

    public override IDisposable OnScheduleExecution(Func<ScheduleExecutionEvent, Task> handler)
    {
        _scheduleHandlers.Add(handler);
        return new ActionDisposable(() => _scheduleHandlers.Remove(handler));
    }

    public override IDisposable OnHealthUpdate(Func<ServiceHealthUpdate, Task> handler)
    {
        _healthHandlers.Add(handler);
        return new ActionDisposable(() => _healthHandlers.Remove(handler));
    }

    public override IDisposable OnMemoryRecall(Func<MemoryRecallEvent, Task> handler)
    {
        _recallHandlers.Add(handler);
        return new ActionDisposable(() => _recallHandlers.Remove(handler));
    }

    public override IDisposable OnMemoryExtraction(Func<MemoryExtractionEvent, Task> handler)
    {
        _extractionHandlers.Add(handler);
        return new ActionDisposable(() => _extractionHandlers.Remove(handler));
    }

    public override IDisposable OnMemoryDreaming(Func<MemoryDreamingEvent, Task> handler)
    {
        _dreamingHandlers.Add(handler);
        return new ActionDisposable(() => _dreamingHandlers.Remove(handler));
    }

    public override IDisposable OnContextTruncation(Func<ContextTruncationEvent, Task> handler)
    {
        _truncationHandlers.Add(handler);
        return new ActionDisposable(() => _truncationHandlers.Remove(handler));
    }

    public override IDisposable OnLatency(Func<LatencyEvent, Task> handler)
    {
        _latencyHandlers.Add(handler);
        return new ActionDisposable(() => _latencyHandlers.Remove(handler));
    }

    public override IDisposable OnVoice(Func<VoiceEvent, Task> handler)
    {
        _voiceHandlers.Add(handler);
        return new ActionDisposable(() => _voiceHandlers.Remove(handler));
    }

    public override void OnReconnected(Func<string?, Task> handler) { }
    public override void OnClosed(Func<Exception?, Task> handler) { }
    public override void OnReconnecting(Func<Exception?, Task> handler) { }

    public override Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task FireTokenUsage(TokenUsageEvent evt) =>
        Task.WhenAll(_tokenHandlers.Select(h => h(evt)));

    public Task FireToolCall(ToolCallEvent evt) =>
        Task.WhenAll(_toolHandlers.Select(h => h(evt)));

    public Task FireError(ErrorEvent evt) =>
        Task.WhenAll(_errorHandlers.Select(h => h(evt)));

    public Task FireScheduleExecution(ScheduleExecutionEvent evt) =>
        Task.WhenAll(_scheduleHandlers.Select(h => h(evt)));

    public Task FireMemoryRecall(MemoryRecallEvent evt) =>
        Task.WhenAll(_recallHandlers.Select(h => h(evt)));

    public Task FireMemoryExtraction(MemoryExtractionEvent evt) =>
        Task.WhenAll(_extractionHandlers.Select(h => h(evt)));

    public Task FireMemoryDreaming(MemoryDreamingEvent evt) =>
        Task.WhenAll(_dreamingHandlers.Select(h => h(evt)));

    public Task FireContextTruncation(ContextTruncationEvent evt) =>
        Task.WhenAll(_truncationHandlers.Select(h => h(evt)));

    public Task FireLatency(LatencyEvent evt) =>
        Task.WhenAll(_latencyHandlers.Select(h => h(evt)));

    public Task FireVoice(VoiceEvent evt) =>
        Task.WhenAll(_voiceHandlers.Select(h => h(evt)));

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly Queue<(object Data, TimeSpan Delay)> _responses = new();

    public string? LastRequestUri { get; private set; }

    // Concurrent bag, not a List<T>: DataLoadEffect fires ~19 requests via Task.WhenAll, so
    // multiple SendAsync calls can race on this collection.
    public ConcurrentBag<string?> Requests { get; } = [];

    public void EnqueueResponse<T>(T data, TimeSpan delay)
    {
        _responses.Enqueue((data!, delay));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        Requests.Add(LastRequestUri);

        if (_responses.TryDequeue(out var entry))
        {
            if (entry.Delay > TimeSpan.Zero)
            {
                await Task.Delay(entry.Delay, cancellationToken);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(entry.Data);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}