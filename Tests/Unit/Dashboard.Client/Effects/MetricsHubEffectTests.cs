using Dashboard.Client.Effects;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Domain.DTOs.Metrics;
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
    private readonly MetricsHubEffect _effect;

    public MetricsHubEffectTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var api = new MetricsApiService(http);
        _effect = new MetricsHubEffect(
            _hub, api, _metricsStore, _healthStore,
            _tokensStore, _toolsStore, _errorsStore,
            _schedulesStore, _connectionStore);
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
    }

    [Fact]
    public async Task RefreshTokenBreakdown_RapidEvents_CancelsStaleApiCall()
    {
        await _effect.StartAsync();

        var staleData = new Dictionary<string, decimal> { ["stale-model"] = 100m };
        var freshData = new Dictionary<string, decimal> { ["fresh-model"] = 200m };

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var evt = new TokenUsageEvent
        {
            Sender = "test", Model = "m", InputTokens = 1, OutputTokens = 1, Cost = 0.01m,
        };

        var task1 = _hub.FireTokenUsage(evt);
        var task2 = _hub.FireTokenUsage(evt);
        await Task.WhenAll(task1, task2);
        await Task.Delay(100);

        _tokensStore.State.Breakdown.ShouldBe(freshData);
    }

    [Fact]
    public async Task RefreshToolBreakdown_RapidEvents_CancelsStaleApiCall()
    {
        await _effect.StartAsync();

        var staleData = new Dictionary<string, decimal> { ["stale-tool"] = 10m };
        var freshData = new Dictionary<string, decimal> { ["fresh-tool"] = 20m };

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var evt = new ToolCallEvent
        {
            ToolName = "t", Success = true, DurationMs = 100,
        };

        var task1 = _hub.FireToolCall(evt);
        var task2 = _hub.FireToolCall(evt);
        await Task.WhenAll(task1, task2);
        await Task.Delay(100);

        _toolsStore.State.Breakdown.ShouldBe(freshData);
    }

    [Fact]
    public async Task RefreshErrorBreakdown_RapidEvents_CancelsStaleApiCall()
    {
        await _effect.StartAsync();

        var staleData = new Dictionary<string, int> { ["stale-err"] = 5 };
        var freshData = new Dictionary<string, int> { ["fresh-err"] = 10 };

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var evt = new ErrorEvent
        {
            Message = "err", Service = "s", ErrorType = "e",
        };

        var task1 = _hub.FireError(evt);
        var task2 = _hub.FireError(evt);
        await Task.WhenAll(task1, task2);
        await Task.Delay(100);

        _errorsStore.State.Breakdown.ShouldBe(freshData);
    }

    [Fact]
    public async Task RefreshScheduleBreakdown_RapidEvents_CancelsStaleApiCall()
    {
        await _effect.StartAsync();

        var staleData = new Dictionary<string, int> { ["stale-sched"] = 3 };
        var freshData = new Dictionary<string, int> { ["fresh-sched"] = 7 };

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var evt = new ScheduleExecutionEvent
        {
            ScheduleId = "s", Prompt = "p", Success = true, DurationMs = 50,
        };

        var task1 = _hub.FireScheduleExecution(evt);
        var task2 = _hub.FireScheduleExecution(evt);
        await Task.WhenAll(task1, task2);
        await Task.Delay(100);

        _schedulesStore.State.Breakdown.ShouldBe(freshData);
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

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly Queue<(object Data, TimeSpan Delay)> _responses = new();

    public void EnqueueResponse<T>(T data, TimeSpan delay)
    {
        _responses.Enqueue((data!, delay));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.TryDequeue(out var entry))
        {
            await Task.Delay(entry.Delay, cancellationToken);
            var json = System.Text.Json.JsonSerializer.Serialize(entry.Data);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}
