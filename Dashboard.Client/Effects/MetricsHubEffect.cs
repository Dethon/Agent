using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;

namespace Dashboard.Client.Effects;

public sealed class MetricsHubEffect(
    MetricsHubService hub,
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore,
    TokensStore tokensStore,
    ToolsStore toolsStore,
    ErrorsStore errorsStore,
    SchedulesStore schedulesStore,
    ConnectionStore connectionStore) : IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    private async Task RefreshTokenBreakdownAsync()
    {
        try
        {
            var s = tokensStore.State;
            var result = await api.GetTokenGroupedAsync(s.GroupBy, s.Metric, s.From, s.To);
            tokensStore.SetBreakdown(result ?? []);
        }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshToolBreakdownAsync()
    {
        try
        {
            var s = toolsStore.State;
            var result = await api.GetToolGroupedAsync(s.GroupBy, s.Metric, s.From, s.To);
            toolsStore.SetBreakdown(result ?? []);
        }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshErrorBreakdownAsync()
    {
        try
        {
            var s = errorsStore.State;
            var result = await api.GetErrorGroupedAsync(s.GroupBy, s.From, s.To);
            errorsStore.SetBreakdown(result ?? []);
        }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshScheduleBreakdownAsync()
    {
        try
        {
            var s = schedulesStore.State;
            var result = await api.GetScheduleGroupedAsync(s.GroupBy, s.From, s.To);
            schedulesStore.SetBreakdown(result ?? []);
        }
        catch { /* Breakdown stays at last known value */ }
    }

    private bool _started;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        _subscriptions.Add(hub.OnTokenUsage(async evt =>
        {
            metricsStore.IncrementFromTokenUsage(evt);
            tokensStore.AppendEvent(evt);
            await RefreshTokenBreakdownAsync();
        }));

        _subscriptions.Add(hub.OnToolCall(async evt =>
        {
            metricsStore.IncrementToolCall(!evt.Success);
            toolsStore.AppendEvent(evt);
            await RefreshToolBreakdownAsync();
        }));

        _subscriptions.Add(hub.OnError(async evt =>
        {
            errorsStore.AppendEvent(evt);
            await RefreshErrorBreakdownAsync();
        }));

        _subscriptions.Add(hub.OnScheduleExecution(async evt =>
        {
            schedulesStore.AppendEvent(evt);
            await RefreshScheduleBreakdownAsync();
        }));

        _subscriptions.Add(hub.OnHealthUpdate(evt =>
        {
            var current = healthStore.State.Services.ToList();
            var idx = current.FindIndex(s => s.Service == evt.Service);
            var entry = new ServiceHealth(evt.Service, evt.IsHealthy, evt.Timestamp.ToString("o"));

            if (idx >= 0)
            {
                current[idx] = entry;
            }
            else
            {
                current.Add(entry);
            }

            healthStore.UpdateHealth(current);
            return Task.CompletedTask;
        }));

        hub.Reconnected += _ =>
        {
            connectionStore.SetConnected(true);
            return Task.CompletedTask;
        };

        hub.Closed += _ =>
        {
            connectionStore.SetConnected(false);
            return Task.CompletedTask;
        };

        hub.Reconnecting += _ =>
        {
            connectionStore.SetConnected(false);
            return Task.CompletedTask;
        };

        await hub.StartAsync();
        connectionStore.SetConnected(true);
    }

    public async ValueTask DisposeAsync()
    {
        _subscriptions.ForEach(s => s.Dispose());
        _subscriptions.Clear();
        await hub.DisposeAsync();
    }
}
