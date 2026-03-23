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
    MetricsStore metricsStore,
    HealthStore healthStore,
    TokensStore tokensStore,
    ToolsStore toolsStore,
    ErrorsStore errorsStore,
    SchedulesStore schedulesStore,
    ConnectionStore connectionStore) : IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    public async Task StartAsync()
    {
        _subscriptions.Add(hub.OnTokenUsage(evt =>
        {
            metricsStore.IncrementFromTokenUsage(evt);
            tokensStore.AppendEvent(evt);
        }));

        _subscriptions.Add(hub.OnToolCall(evt =>
        {
            metricsStore.IncrementToolCall(!evt.Success);
            toolsStore.AppendEvent(evt);
        }));

        _subscriptions.Add(hub.OnError(evt =>
        {
            errorsStore.AppendEvent(evt);
        }));

        _subscriptions.Add(hub.OnScheduleExecution(evt =>
        {
            schedulesStore.AppendEvent(evt);
        }));

        _subscriptions.Add(hub.OnHealthUpdate(evt =>
        {
            var current = healthStore.State.Services.ToList();
            var idx = current.FindIndex(s => s.Service == evt.Service);
            var entry = new ServiceHealth(evt.Service, true, evt.Timestamp.ToString("o"));

            if (idx >= 0)
                current[idx] = entry;
            else
                current.Add(entry);

            healthStore.UpdateHealth(current);
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
