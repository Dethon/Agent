using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;

namespace Dashboard.Client.Effects;

public sealed class MetricsHubEffect(
    MetricsHubService hub,
    MetricFamilyTable families,
    MetricsStore metricsStore,
    HealthStore healthStore,
    ConnectionStore connectionStore) : IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private bool _started;

    // The live-update path's failure policy, written once: a refresh that fails leaves the family's
    // breakdown at its last known value.
    private static async Task RefreshAsync(MetricFamily family)
    {
        try
        {
            await family.RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        _subscriptions.Add(hub.OnMemoryRecall(async evt =>
        {
            metricsStore.IncrementMemoryRecall(evt.MemoryCount);
            families.Memory.Store.AppendRecallEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.OnMemoryExtraction(async evt =>
        {
            metricsStore.IncrementMemoryExtraction(evt.StoredCount);
            families.Memory.Store.AppendExtractionEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.OnMemoryDreaming(async evt =>
        {
            metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
            families.Memory.Store.AppendDreamingEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.OnTokenUsage(async evt =>
        {
            metricsStore.IncrementFromTokenUsage(evt);
            families.Tokens.Store.AppendEvent(evt);
            await RefreshAsync(families.Tokens);
        }));

        _subscriptions.Add(hub.OnContextTruncation(async _ =>
        {
            families.Tokens.Store.IncrementTruncations();
            await RefreshAsync(families.Tokens);
        }));

        _subscriptions.Add(hub.OnToolCall(async evt =>
        {
            metricsStore.IncrementToolCall(!evt.Success);
            families.Tools.Store.AppendEvent(evt);
            await RefreshAsync(families.Tools);
        }));

        _subscriptions.Add(hub.OnError(async evt =>
        {
            families.Errors.Store.AppendEvent(evt);
            await RefreshAsync(families.Errors);
        }));

        _subscriptions.Add(hub.OnScheduleExecution(async evt =>
        {
            families.Schedules.Store.AppendEvent(evt);
            await RefreshAsync(families.Schedules);
        }));

        _subscriptions.Add(hub.OnLatency(async evt =>
        {
            families.Latency.Store.AppendEvent(evt);
            await RefreshAsync(families.Latency);
        }));

        _subscriptions.Add(hub.OnVoice(async evt =>
        {
            families.Voice.Store.AppendEvent(evt);
            await RefreshAsync(families.Voice);
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

        hub.OnReconnected(_ =>
        {
            connectionStore.SetConnected(true);
            return Task.CompletedTask;
        });

        hub.OnClosed(_ =>
        {
            connectionStore.SetConnected(false);
            return Task.CompletedTask;
        });

        hub.OnReconnecting(_ =>
        {
            connectionStore.SetConnected(false);
            return Task.CompletedTask;
        });

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