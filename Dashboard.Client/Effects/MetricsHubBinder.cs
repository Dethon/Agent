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
    private List<Func<Task>>? _held;

    // Catch-up replaces the event lists wholesale while pushes keep arriving: a push applied before
    // the response lands is erased by the older snapshot, and one the snapshot already contains
    // would be appended on top of its own copy. So the live connection holds pushes for the
    // duration of a catch-up and releases them against the reloaded lists, where each held event is
    // skipped exactly when the snapshot already delivered it — record value equality is the
    // identity.
    public void HoldPushes() => _held = [];

    public async Task ReleaseHeldPushesAsync()
    {
        var held = _held;
        _held = null;

        foreach (var deliver in held ?? [])
        {
            await deliver();
        }
    }

    private Func<T, Task> OnPush<T>(Func<T, bool> alreadyCaughtUp, Func<T, Task> apply) =>
        evt =>
        {
            if (_held is { } held)
            {
                // The dedupe question is asked on release, once the snapshot has written the list
                // this event may already be in.
                held.Add(() => alreadyCaughtUp(evt) ? Task.CompletedTask : apply(evt));
                return Task.CompletedTask;
            }

            return apply(evt);
        };

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

        _subscriptions.Add(hub.On("OnMemoryRecall", OnPush<MemoryRecallEvent>(
            evt => families.Memory.Store.State.RecallEvents.Contains(evt),
            async evt =>
            {
                metricsStore.IncrementMemoryRecall(evt.MemoryCount);
                families.Memory.Store.AppendRecallEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnMemoryExtraction", OnPush<MemoryExtractionEvent>(
            evt => families.Memory.Store.State.ExtractionEvents.Contains(evt),
            async evt =>
            {
                metricsStore.IncrementMemoryExtraction(evt.StoredCount);
                families.Memory.Store.AppendExtractionEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnMemoryDreaming", OnPush<MemoryDreamingEvent>(
            evt => families.Memory.Store.State.DreamingEvents.Contains(evt),
            async evt =>
            {
                metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
                families.Memory.Store.AppendDreamingEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnTokenUsage", OnPush<TokenUsageEvent>(
            evt => families.Tokens.Store.State.Events.Contains(evt),
            async evt =>
            {
                metricsStore.IncrementFromTokenUsage(evt);
                families.Tokens.Store.AppendEvent(evt);
                await RefreshAsync(families.Tokens);
            })));

        // A truncation is a counter bump with no event to reconcile by. Catch-up reloads the total
        // from the server, so a held bump is dropped rather than doubled on top of that total; one
        // truncated turn missed here corrects itself on the next load.
        _subscriptions.Add(hub.On("OnContextTruncation", OnPush<ContextTruncationEvent>(
            _ => true,
            async _ =>
            {
                families.Tokens.Store.IncrementTruncations();
                await RefreshAsync(families.Tokens);
            })));

        _subscriptions.Add(hub.On("OnToolCall", OnPush<ToolCallEvent>(
            evt => families.Tools.Store.State.Events.Contains(evt),
            async evt =>
            {
                metricsStore.IncrementToolCall(!evt.Success);
                families.Tools.Store.AppendEvent(evt);
                await RefreshAsync(families.Tools);
            })));

        _subscriptions.Add(hub.On("OnError", OnPush<ErrorEvent>(
            evt => families.Errors.Store.State.Events.Contains(evt),
            async evt =>
            {
                families.Errors.Store.AppendEvent(evt);
                await RefreshAsync(families.Errors);
            })));

        _subscriptions.Add(hub.On("OnScheduleExecution", OnPush<ScheduleExecutionEvent>(
            evt => families.Schedules.Store.State.Events.Contains(evt),
            async evt =>
            {
                families.Schedules.Store.AppendEvent(evt);
                await RefreshAsync(families.Schedules);
            })));

        _subscriptions.Add(hub.On("OnLatency", OnPush<LatencyEvent>(
            evt => families.Latency.Store.State.Events.Contains(evt),
            async evt =>
            {
                families.Latency.Store.AppendEvent(evt);
                await RefreshAsync(families.Latency);
            })));

        _subscriptions.Add(hub.On("OnVoice", OnPush<VoiceEvent>(
            evt => families.Voice.Store.State.Events.Contains(evt),
            async evt =>
            {
                families.Voice.Store.AppendEvent(evt);
                await RefreshAsync(families.Voice);
            })));

        // Health is an upsert the catch-up walk never touches, so it is never held.

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