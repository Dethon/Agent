using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Domain.Channels;

public sealed class ChannelInbox(
    TimeProvider? timeProvider = null,
    int capacity = 256,
    TimeSpan? subscriberIdleTimeout = null,
    ILogger<ChannelInbox>? logger = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Only an *empty* subscriber is ever evicted, so this bounds nothing but abandoned bookkeeping.
    // A healthy agent touches its subscriber at least every ~60s (a fully held 30s poll plus the
    // 30s retry backoff ceiling), so an hour is ~60x any legitimate gap.
    private readonly TimeSpan _idleTimeout = subscriberIdleTimeout ?? TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    private readonly int _capacity = ValidateCapacity(capacity);

    // The only liveness question this type answers, and deliberately so. "Is there bookkeeping for
    // this id" is true for up to an hour after a subscriber goes quiet — precisely so a channel
    // outage doesn't discard what was buffered during it (see PruneIdle) — which makes it the wrong
    // question for a caller about to act on "delivery": gating a destructive action (deleting a
    // schedule, dropping a routing entry) on it would treat an item merely sitting in an idle buffer
    // as delivered. HasLiveSubscriber asks whether *someone actually polled recently*; a subscriber
    // holding items but not repolling does not count, which is the case this method exists to
    // distinguish. A near-miss twin of this method is how that distinction gets lost again.
    public bool HasLiveSubscriber(TimeSpan freshness)
    {
        PruneIdle();
        var cutoff = _timeProvider.GetUtcNow() - freshness;
        return _subscribers.Values.Any(subscriber => subscriber.IsLiveSince(cutoff));
    }

    public void Enqueue(ChannelInboxItem item)
    {
        PruneIdle();
        var dropped = _subscribers
            .Where(entry => entry.Value.Enqueue(item, _capacity) == EnqueueOutcome.AcceptedDroppingOldest)
            .Select(entry => entry.Key)
            .ToArray();
        WarnDroppedOldest(dropped);
    }

    // The targeted variant, for channels whose delivery policy is buffer-always (Telegram): unlike
    // the broadcast Enqueue it creates the subscriber's queue on demand, so a message arriving
    // before the agent's first poll — server cold start, or just after an idle eviction — is
    // buffered instead of fanned out to nobody. Creation is bookkeeping only: the subscriber does
    // not count as live until someone actually polls it, and capacity remains the only bound.
    public void EnqueueFor(string subscriberId, ChannelInboxItem item)
    {
        PruneIdle();
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            // Same seeding rationale as ReceiveAsync: left at default, the stamp would sit behind
            // every cutoff and a concurrent prune could retire and remove the instance this call
            // just created before the item lands in it.
            var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber(now));
            var outcome = subscriber.Enqueue(item, _capacity);
            if (outcome != EnqueueOutcome.Refused)
            {
                if (outcome == EnqueueOutcome.AcceptedDroppingOldest)
                {
                    WarnDroppedOldest([subscriberId]);
                }

                return;
            }

            // A concurrent prune retired this instance between the lookup and the enqueue; finish
            // its removal rather than hand the item to a queue nobody drains. Each pass drops the
            // instance it observed, so the loop is bounded.
            _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriberId, subscriber));
        }
    }

    private void WarnDroppedOldest(IReadOnlyList<string> subscriberIds)
    {
        if (subscriberIds.Count > 0)
        {
            // Capacity is the only bound on what an outage can buffer, so crossing it is the
            // moment a message is irrecoverably lost — the one line that must not pass silently.
            logger?.LogWarning(
                "Inbox at capacity ({Capacity}); dropped the oldest buffered item for {SubscriberIds}",
                _capacity, string.Join(", ", subscriberIds));
        }
    }

    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
        string subscriberId,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            // Seeding the new subscriber with `now` matters: left at default it would sit behind
            // every cutoff, so a concurrent Enqueue's prune could retire and remove it in the window
            // before TryTouch and then broadcast to a snapshot that excludes it — dropping the item.
            var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber(now));
            if (subscriber.TryTouch(now))
            {
                return subscriber.ReceiveAsync(maxWait, _timeProvider, ct);
            }

            // A concurrent prune retired this instance between the lookup and the touch. Seeding
            // makes that unreachable for a subscriber this call created, so only one already past
            // the cutoff can land here; finish its removal rather than poll a subscriber Enqueue can
            // no longer reach. Each pass drops the instance it observed, so the loop is bounded.
            _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriberId, subscriber));
        }
    }

    private static int ValidateCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return capacity;
    }

    // Eviction exists only to stop the subscriber map growing without bound; it must never be the
    // thing that loses a message. A subscriber still holding items is kept however long it has been
    // idle, so a channel outage of any length is survivable — capacity, not time, is the bound.
    private void PruneIdle()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var retired = _subscribers.Where(kv => kv.Value.TryRetire(cutoff)).ToArray();
        foreach (var entry in retired)
        {
            _subscribers.TryRemove(entry);
        }
    }

    private enum EnqueueOutcome
    {
        // The retirement latch refused the item; the caller must not treat it as buffered.
        Refused,
        Accepted,
        AcceptedDroppingOldest
    }

    private sealed class Subscriber(DateTimeOffset createdAt)
    {
        private readonly Lock _gate = new();
        private readonly Queue<ChannelInboxItem> _items = new();
        private TaskCompletionSource<bool>? _waiter;
        private DateTimeOffset _lastPolledAt = createdAt;
        private bool _hasPolled;
        private bool _retired;

        public bool TryTouch(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _lastPolledAt = now;
                _hasPolled = true;
                return true;
            }
        }

        // Deliberately ignores _items: a subscriber that is only holding buffered items but hasn't
        // repolled since the cutoff is exactly the stale-buffer case HasLiveSubscriber must reject.
        // Requiring an actual poll matters for the same reason: a queue minted by EnqueueFor has a
        // fresh seed stamp but no poller yet, and reading it as live would hand the stale-buffer
        // bug right back to any caller gating a destructive action on liveness.
        public bool IsLiveSince(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                return !_retired && _hasPolled && _lastPolledAt >= cutoff;
            }
        }

        // Both conditions are read under the same lock that Enqueue and Drain mutate them under, and
        // the retirement is latched in the same critical section: an item can never be accepted into
        // a queue that a prune has already decided to throw away.
        //
        // A subscriber is stamped when its poll *starts*, so "a touch protects for one idle timeout"
        // only holds while the caller's maxWait stays below that timeout — otherwise a poll could be
        // retired while still parked on it, and items arriving before it re-polls would reach no
        // subscriber. maxWaitMs is caller-supplied (ChannelReceiveTool), 30s against a 1h timeout
        // today, so there is decades of headroom; a caller closing that gap would have to widen the
        // timeout to match.
        public bool TryRetire(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                if (_retired || _items.Count > 0 || _lastPolledAt >= cutoff)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }

        public EnqueueOutcome Enqueue(ChannelInboxItem item, int capacity)
        {
            TaskCompletionSource<bool>? toSignal;
            var droppedOldest = false;
            lock (_gate)
            {
                // Refusing here is what makes retirement safe: a prune that has already latched this
                // subscriber must not be handed an item by a thread whose _subscribers snapshot
                // predates the removal, or that item would be accepted into a queue nobody drains.
                if (_retired)
                {
                    return EnqueueOutcome.Refused;
                }

                if (_items.Count >= capacity)
                {
                    _items.Dequeue();
                    droppedOldest = true;
                }

                _items.Enqueue(item);
                toSignal = _waiter;
                _waiter = null;
            }

            toSignal?.TrySetResult(true);
            return droppedOldest ? EnqueueOutcome.AcceptedDroppingOldest : EnqueueOutcome.Accepted;
        }

        public async Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
            TimeSpan maxWait,
            TimeProvider timeProvider,
            CancellationToken ct)
        {
            // Same contract as the post-wait check below, on the path that never waits: a poll whose
            // caller has already hung up must not drain. ReconnectAsync aborts a poll and issues a
            // fresh one immediately behind it, so an already-cancelled token reaching the fast path
            // is routine — and draining there hands the batch to a dead request and loses it.
            ct.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> waiter;
            TaskCompletionSource<bool>? displaced;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    return Drain();
                }

                displaced = _waiter;
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = waiter;
            }

            // A second poll for the same subscriber retires the first with an empty batch,
            // otherwise two waiters would split the stream between them.
            displaced?.TrySetResult(false);

            if (maxWait <= TimeSpan.Zero)
            {
                return RetireAndDrain(waiter);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(maxWait, timeProvider, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter.Task, delay);
            await timeoutCts.CancelAsync();

            if (ct.IsCancellationRequested)
            {
                // The caller is gone — draining here would hand the batch to an aborted request and
                // lose it. Leave the items queued for the next poll and surface the cancellation.
                lock (_gate)
                {
                    RetireWaiter(waiter);
                }

                ct.ThrowIfCancellationRequested();
            }

            if (completed == waiter.Task && !waiter.Task.Result)
            {
                return [];
            }

            return RetireAndDrain(waiter);
        }

        private IReadOnlyList<ChannelInboxItem> RetireAndDrain(TaskCompletionSource<bool> waiter)
        {
            lock (_gate)
            {
                RetireWaiter(waiter);
                return Drain();
            }
        }

        // Caller holds _gate. Only the poll that registered a waiter may retire it: a poll that
        // blindly nulls _waiter can drop the waiter a *later* poll registered while this one was
        // resuming, leaving the next Enqueue with nobody to signal — that poll then sleeps out its
        // whole maxWait with items already sitting in its queue.
        private void RetireWaiter(TaskCompletionSource<bool> waiter)
        {
            if (ReferenceEquals(_waiter, waiter))
            {
                _waiter = null;
            }
        }

        private IReadOnlyList<ChannelInboxItem> Drain()
        {
            var drained = _items.ToArray();
            _items.Clear();
            return drained;
        }
    }
}