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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Services;

public sealed class MetricsLiveConnectionTests : IAsyncDisposable
{
    private readonly FakeMetricsHubConnection _hub = new();
    private readonly FakeApiHandler _handler = new();
    private readonly FakeTimeProvider _time = new();
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
    private readonly MetricFamilyTable _families;
    private readonly MetricsLiveConnection _liveConnection;

    public MetricsLiveConnectionTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _families = new MetricFamilyTable(
            new MetricsApiService(http), _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        var binder = new MetricsHubEffect(_families, _metricsStore, _healthStore);
        _liveConnection = new MetricsLiveConnection(
            _hub, binder, _connectionStore, _time, NullLogger<MetricsLiveConnection>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _liveConnection.DisposeAsync();
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

    // The retry loop delays through the injected time provider, so nothing here waits in real time:
    // push the clock forward until the connect the module is driving on its own has completed.
    private async Task ConnectAsync()
    {
        var connecting = _liveConnection.ConnectAsync();

        foreach (var _ in Enumerable.Range(0, 50))
        {
            if (connecting.IsCompleted)
            {
                break;
            }

            _time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(1);
        }

        await connecting;
    }

    private Task RaiseVoiceAsync(string satelliteId) =>
        _hub.RaiseAsync("OnVoice", new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceTranscribed,
            SatelliteId = satelliteId,
        });

    [Fact]
    public async Task ConnectAsync_FirstConnect_AServerPushReachesTheStore()
    {
        await ConnectAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    [Fact]
    public async Task ConnectAsync_FirstConnect_ReportsTheDashboardConnected()
    {
        await ConnectAsync();

        _connectionStore.State.IsConnected.ShouldBeTrue();
    }

    // A dashboard opened while the agent is still starting: nobody up the stack retries, and the
    // module connects on its own once the hub answers.
    [Fact]
    public async Task ConnectAsync_TheHubIsUnavailableAtFirst_ConnectsOnItsOwnWithNoCallerRetrying()
    {
        _hub.FailedStartsRemaining = 3;

        await ConnectAsync();

        _hub.StartAttempts.ShouldBe(4);
        _connectionStore.State.IsConnected.ShouldBeTrue();
    }

    // The old defect in one assertion: the started latch was set before the work, so a failed first
    // start left the module believing it was running and every later call did nothing.
    [Fact]
    public async Task ConnectAsync_AFailedStart_DoesNotLockTheModuleOut()
    {
        _hub.FailedStartsRemaining = 2;

        await ConnectAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    // Handlers are bound once, before the first start attempt. Rebinding per attempt would leave
    // every handler registered as many times as the start was retried.
    [Fact]
    public async Task ConnectAsync_AfterRetriedStarts_BindsEachHandlerOnce()
    {
        _hub.FailedStartsRemaining = 3;

        await ConnectAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.Count(e => e.SatelliteId == "kitchen-01").ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_AfterAReconnect_AServerPushStillReachesTheStore()
    {
        await ConnectAsync();
        await _hub.RaiseReconnectingAsync(null);
        await _hub.RaiseReconnectedAsync();

        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldContain(e => e.SatelliteId == "kitchen-01");
    }

    [Fact]
    public async Task ConnectAsync_TheTransportIsReconnecting_ReportsTheDashboardNotConnected()
    {
        await ConnectAsync();

        await _hub.RaiseReconnectingAsync(null);

        _connectionStore.State.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task DisposeAsync_AfterConnecting_APushAfterwardsChangesNothing()
    {
        await ConnectAsync();

        await _liveConnection.DisposeAsync();
        await RaiseVoiceAsync("kitchen-01");

        _voiceStore.State.Events.ShouldBeEmpty();
        _hub.Disposed.ShouldBeTrue();
    }
}