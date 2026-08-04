using Dashboard.Client.Contracts;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Domain.DTOs.Metrics;

namespace Dashboard.Client.Effects;

// The mapping from a server push to a store update and a family refresh, and nothing else. The
// connection lifecycle belongs to MetricsLiveConnection, which drives Bind and Unbind.
public sealed class MetricsHubBinder(
    MetricFamilyTable families,
    MetricsStore metricsStore,
    HealthStore healthStore)
{
    private readonly List<IDisposable> _subscriptions = [];

    // The live-update path's failure policy, written once: a refresh that fails leaves the family's
    // breakdown at its last known value. Nothing cancels a refresh any more, so a request abandoned
    // on an HTTP timeout settles the same way as any other failure and needs no arm of its own.
    private static async Task RefreshAsync(MetricFamily family)
    {
        try
        {
            await family.RefreshAsync();
        }
        catch { /* Breakdown stays at last known value */ }
    }

    public void Bind(IMetricsHubConnection hub)
    {
        ArgumentNullException.ThrowIfNull(hub);

        _subscriptions.Add(hub.On<MemoryRecallEvent>("OnMemoryRecall", async evt =>
        {
            metricsStore.IncrementMemoryRecall(evt.MemoryCount);
            families.Memory.Store.AppendRecallEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.On<MemoryExtractionEvent>("OnMemoryExtraction", async evt =>
        {
            metricsStore.IncrementMemoryExtraction(evt.StoredCount);
            families.Memory.Store.AppendExtractionEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.On<MemoryDreamingEvent>("OnMemoryDreaming", async evt =>
        {
            metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
            families.Memory.Store.AppendDreamingEvent(evt);
            await RefreshAsync(families.Memory);
        }));

        _subscriptions.Add(hub.On<TokenUsageEvent>("OnTokenUsage", async evt =>
        {
            metricsStore.IncrementFromTokenUsage(evt);
            families.Tokens.Store.AppendEvent(evt);
            await RefreshAsync(families.Tokens);
        }));

        _subscriptions.Add(hub.On<ContextTruncationEvent>("OnContextTruncation", async _ =>
        {
            families.Tokens.Store.IncrementTruncations();
            await RefreshAsync(families.Tokens);
        }));

        _subscriptions.Add(hub.On<ToolCallEvent>("OnToolCall", async evt =>
        {
            metricsStore.IncrementToolCall(!evt.Success);
            families.Tools.Store.AppendEvent(evt);
            await RefreshAsync(families.Tools);
        }));

        _subscriptions.Add(hub.On<ErrorEvent>("OnError", async evt =>
        {
            families.Errors.Store.AppendEvent(evt);
            await RefreshAsync(families.Errors);
        }));

        _subscriptions.Add(hub.On<ScheduleExecutionEvent>("OnScheduleExecution", async evt =>
        {
            families.Schedules.Store.AppendEvent(evt);
            await RefreshAsync(families.Schedules);
        }));

        _subscriptions.Add(hub.On<LatencyEvent>("OnLatency", async evt =>
        {
            families.Latency.Store.AppendEvent(evt);
            await RefreshAsync(families.Latency);
        }));

        _subscriptions.Add(hub.On<VoiceEvent>("OnVoice", async evt =>
        {
            families.Voice.Store.AppendEvent(evt);
            await RefreshAsync(families.Voice);
        }));

        _subscriptions.Add(hub.On<ServiceHealthUpdate>("OnHealthUpdate", evt =>
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

    }

    public void Unbind()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _subscriptions.Clear();
    }
}