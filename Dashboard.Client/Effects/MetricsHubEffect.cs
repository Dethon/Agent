using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Memory;
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
    ConnectionStore connectionStore,
    MemoryStore memoryStore) : IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    private CancellationTokenSource _tokenBreakdownCts = new();
    private CancellationTokenSource _toolBreakdownCts = new();
    private CancellationTokenSource _errorBreakdownCts = new();
    private CancellationTokenSource _scheduleBreakdownCts = new();
    private CancellationTokenSource _memoryBreakdownCts = new();

    private CancellationToken ResetCts(ref CancellationTokenSource cts)
    {
        cts.Cancel();
        cts.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }

    private async Task RefreshTokenBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = tokensStore.State;
            var result = await api.GetTokenGroupedAsync(s.GroupBy, s.Metric, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            tokensStore.SetBreakdown(result ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshToolBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = toolsStore.State;
            var result = await api.GetToolGroupedAsync(s.GroupBy, s.Metric, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            toolsStore.SetBreakdown(result ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshErrorBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = errorsStore.State;
            var result = await api.GetErrorGroupedAsync(s.GroupBy, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            errorsStore.SetBreakdown(result ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshScheduleBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = schedulesStore.State;
            var result = await api.GetScheduleGroupedAsync(s.GroupBy, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            schedulesStore.SetBreakdown(result ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    private async Task RefreshMemoryBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = memoryStore.State;
            var result = await api.GetMemoryGroupedAsync(s.GroupBy, s.Metric, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            memoryStore.SetBreakdown(result ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* Breakdown stays at last known value */ }
    }

    private bool _started;

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
            memoryStore.AppendRecallEvent(evt);
            var ct = ResetCts(ref _memoryBreakdownCts);
            await RefreshMemoryBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnMemoryExtraction(async evt =>
        {
            metricsStore.IncrementMemoryExtraction(evt.StoredCount);
            memoryStore.AppendExtractionEvent(evt);
            var ct = ResetCts(ref _memoryBreakdownCts);
            await RefreshMemoryBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnMemoryDreaming(async evt =>
        {
            metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
            memoryStore.AppendDreamingEvent(evt);
            var ct = ResetCts(ref _memoryBreakdownCts);
            await RefreshMemoryBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnTokenUsage(async evt =>
        {
            metricsStore.IncrementFromTokenUsage(evt);
            tokensStore.AppendEvent(evt);
            var ct = ResetCts(ref _tokenBreakdownCts);
            await RefreshTokenBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnContextTruncation(async evt =>
        {
            tokensStore.IncrementTruncations();
            var ct = ResetCts(ref _tokenBreakdownCts);
            await RefreshTokenBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnToolCall(async evt =>
        {
            metricsStore.IncrementToolCall(!evt.Success);
            toolsStore.AppendEvent(evt);
            var ct = ResetCts(ref _toolBreakdownCts);
            await RefreshToolBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnError(async evt =>
        {
            errorsStore.AppendEvent(evt);
            var ct = ResetCts(ref _errorBreakdownCts);
            await RefreshErrorBreakdownAsync(ct);
        }));

        _subscriptions.Add(hub.OnScheduleExecution(async evt =>
        {
            schedulesStore.AppendEvent(evt);
            var ct = ResetCts(ref _scheduleBreakdownCts);
            await RefreshScheduleBreakdownAsync(ct);
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
        _tokenBreakdownCts.Dispose();
        _toolBreakdownCts.Dispose();
        _errorBreakdownCts.Dispose();
        _scheduleBreakdownCts.Dispose();
        _memoryBreakdownCts.Dispose();
        await hub.DisposeAsync();
    }
}