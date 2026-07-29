using System.Collections.Concurrent;

namespace Domain.Channels;

public sealed class ChannelInbox(
    TimeProvider? timeProvider = null,
    int capacity = 256,
    TimeSpan? subscriberIdleTimeout = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Only an *empty* subscriber is ever evicted, so this bounds nothing but abandoned bookkeeping.
    // A healthy agent touches its subscriber at least every 30s (the long-poll ceiling, which is
    // also the reconnect backoff cap), so an hour is ~120x any legitimate gap.
    private readonly TimeSpan _idleTimeout = subscriberIdleTimeout ?? TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    private readonly int _capacity = ValidateCapacity(capacity);

    public bool HasSubscribers
    {
        get
        {
            PruneIdle();
            return !_subscribers.IsEmpty;
        }
    }

    // HasSubscribers answers "is there bookkeeping for this id" — true for up to an hour after a
    // subscriber goes quiet, precisely so a channel outage doesn't discard what was buffered during
    // it (see PruneIdle). That is the wrong question for a caller about to act on "delivery":
    // gating a destructive action (deleting a schedule, dropping a routing entry) on HasSubscribers
    // would treat an item merely sitting in an idle buffer as delivered. HasLiveSubscriber asks
    // whether *someone actually polled recently* — a subscriber holding items but not repolling
    // does not count, which is the case this method exists to distinguish.
    public bool HasLiveSubscriber(TimeSpan freshness)
    {
        PruneIdle();
        var cutoff = _timeProvider.GetUtcNow() - freshness;
        return _subscribers.Values.Any(subscriber => subscriber.IsLiveSince(cutoff));
    }

    public void Enqueue(ChannelInboxItem item)
    {
        PruneIdle();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Enqueue(item, _capacity);
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

    private sealed class Subscriber(DateTimeOffset createdAt)
    {
        private readonly Lock _gate = new();
        private readonly Queue<ChannelInboxItem> _items = new();
        private TaskCompletionSource<bool>? _waiter;
        private DateTimeOffset _lastPolledAt = createdAt;
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
                return true;
            }
        }

        // Deliberately ignores _items: a subscriber that is only holding buffered items but hasn't
        // repolled since the cutoff is exactly the stale-buffer case HasLiveSubscriber must reject.
        public bool IsLiveSince(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                return !_retired && _lastPolledAt >= cutoff;
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

        public void Enqueue(ChannelInboxItem item, int capacity)
        {
            TaskCompletionSource<bool>? toSignal;
            lock (_gate)
            {
                // Refusing here is what makes retirement safe: a prune that has already latched this
                // subscriber must not be handed an item by a thread whose _subscribers snapshot
                // predates the removal, or that item would be accepted into a queue nobody drains.
                if (_retired)
                {
                    return;
                }

                if (_items.Count >= capacity)
                {
                    _items.Dequeue();
                }

                _items.Enqueue(item);
                toSignal = _waiter;
                _waiter = null;
            }

            toSignal?.TrySetResult(true);
        }

        public async Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
            TimeSpan maxWait,
            TimeProvider timeProvider,
            CancellationToken ct)
        {
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