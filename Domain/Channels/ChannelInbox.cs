using System.Collections.Concurrent;

namespace Domain.Channels;

public sealed class ChannelInbox(
    TimeProvider? timeProvider = null,
    int capacity = 256,
    TimeSpan? subscriberIdleTimeout = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _idleTimeout = subscriberIdleTimeout ?? TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();

    public bool HasSubscribers
    {
        get
        {
            PruneIdle();
            return !_subscribers.IsEmpty;
        }
    }

    public void Enqueue(ChannelInboxItem item)
    {
        PruneIdle();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Enqueue(item, capacity);
        }
    }

    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
        string subscriberId,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber());
        subscriber.Touch(_timeProvider.GetUtcNow());
        return subscriber.ReceiveAsync(maxWait, _timeProvider, ct);
    }

    private void PruneIdle()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var stale = _subscribers.Where(kv => kv.Value.LastPolledAt < cutoff).Select(kv => kv.Key);
        foreach (var key in stale)
        {
            _subscribers.TryRemove(key, out _);
        }
    }

    private sealed class Subscriber
    {
        private readonly Lock _gate = new();
        private readonly Queue<ChannelInboxItem> _items = new();
        private TaskCompletionSource<bool>? _waiter;

        public DateTimeOffset LastPolledAt { get; private set; }

        public void Touch(DateTimeOffset now)
        {
            lock (_gate)
            {
                LastPolledAt = now;
            }
        }

        public void Enqueue(ChannelInboxItem item, int capacity)
        {
            TaskCompletionSource<bool>? toSignal;
            lock (_gate)
            {
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
                lock (_gate)
                {
                    _waiter = null;
                    return Drain();
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(maxWait, timeProvider, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter.Task, delay);
            await timeoutCts.CancelAsync();

            if (completed == waiter.Task && !waiter.Task.Result)
            {
                return [];
            }

            lock (_gate)
            {
                _waiter = null;
                return Drain();
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