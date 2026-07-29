using System.Collections.Concurrent;
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class ChannelInboxTests
{
    private const string Subscriber = "channel-signalr";

    // Every wait in these tests is bounded so a regression fails the run instead of hanging it:
    // the inbox is driven by a FakeTimeProvider that is never advanced, so a poll that is never
    // signalled would otherwise wait forever.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static ChannelInboxItem Message(string conversationId) =>
        ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = "user",
            Content = "hello"
        });

    private static ChannelInboxItem Cancel(string conversationId) =>
        ChannelInboxItem.ForCancel(new ChannelCancelNotification { ConversationId = conversationId });

    [Fact]
    public async Task ReceiveAsync_WithPendingItems_ReturnsImmediately()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_PreservesMessageAndCancelOrdering()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Cancel("c1"));
        inbox.Enqueue(Message("c2"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Kind).ShouldBe(
            [ChannelInboxItemKind.Message, ChannelInboxItemKind.Cancel, ChannelInboxItemKind.Message]);
    }

    [Fact]
    public async Task Enqueue_BeyondCapacity_DropsOldest()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider(), capacity: 2);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));
        inbox.Enqueue(Message("c3"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c2", "c3"]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenEmpty_WakesOnEnqueue()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Give the waiter a moment to register before enqueueing.
        await Task.Delay(50);
        inbox.Enqueue(Message("c1"));

        var batch = await pending;

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_WhenNothingArrives_ReturnsEmptyAfterTimeout()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(31));

        (await pending).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_SecondPollForSameSubscriber_DisplacesFirstWithEmptyBatch()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var first = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);

        var second = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        (await first).ShouldBeEmpty();

        await Task.Delay(50);
        inbox.Enqueue(Message("c1"));
        (await second).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Enqueue_BroadcastsToEverySubscriber()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None);
        await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        (await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
        (await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
    }

    // Eviction is asserted through what it changes rather than through a "do you still have
    // bookkeeping" flag: an evicted subscriber is not in the Enqueue fan-out, so an item published
    // after the timeout reaches nobody and the next poll comes back empty. A subscriber that was
    // not evicted would have buffered it and handed it over here.
    [Fact]
    public async Task Subscriber_WhenIdleAndEmpty_IsEvicted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        // The positive control: while it is registered, the fan-out reaches it.
        inbox.Enqueue(Message("registered"));
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).Count.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(6));
        inbox.Enqueue(Message("after-eviction"));

        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Subscriber_WhenIdleWithQueuedItems_IsNotEvictedAndStillDeliversThem()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));

        // A channel outage outlasting the idle timeout must not discard what was buffered during it.
        // Delivering both items below is the survival claim in full — strictly more than asking
        // whether the subscriber is still registered.
        time.Advance(TimeSpan.FromHours(3));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c1", "c2"]);
    }

    [Fact]
    public async Task Inbox_AfterEvictingAnIdleSubscriber_StillDeliversToAFreshSubscription()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        time.Advance(TimeSpan.FromMinutes(6));

        // Eviction retires the *instance*, so the id must be reusable: a retired subscriber can
        // neither be resurrected by the next poll nor poison its id, and the early return that stops
        // a retired subscriber accepting items must not follow the id to its replacement. The
        // dropped item is what proves the eviction actually happened before the re-subscription —
        // without it, a subscriber that was never evicted would pass this test unchanged.
        inbox.Enqueue(Message("dropped"));
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).ShouldBeEmpty();

        inbox.Enqueue(Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task Subscriber_WhenIdleAfterDrainingItems_IsEvicted()
    {
        using var cts = new CancellationTokenSource(Deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        inbox.Enqueue(Message("c1"));
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).Count.ShouldBe(1);

        // Having once held items must not make a subscriber permanently unevictable.
        time.Advance(TimeSpan.FromMinutes(6));
        inbox.Enqueue(Message("after-eviction"));

        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_WithNonPositiveCapacity_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ChannelInbox(new FakeTimeProvider(), capacity: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new ChannelInbox(new FakeTimeProvider(), capacity: -1));
    }

    [Fact]
    public async Task HasLiveSubscriber_FreshlyPolledSubscriber_IsTrue()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.HasLiveSubscriber(TimeSpan.FromSeconds(60)).ShouldBeTrue();
    }

    [Fact]
    public void HasLiveSubscriber_NoSubscribers_IsFalse()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());

        inbox.HasLiveSubscriber(TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasLiveSubscriber_SubscriberIdlePastFreshnessWindow_IsFalse()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(61));

        inbox.HasLiveSubscriber(TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }

    // The exact regression this method exists to close: PruneIdle keeps a subscriber that is
    // holding items alive for up to an hour so a channel outage survives, but that must not be
    // mistaken for "someone is listening" — a caller gating a destructive action (deleting a
    // schedule, dropping a routing entry) on liveness needs this to read false once nobody has
    // actually polled in a while, even though the queue is non-empty.
    [Fact]
    public async Task HasLiveSubscriber_SubscriberHoldingItemsButNotPolling_IsFalse()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromHours(1));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        time.Advance(TimeSpan.FromSeconds(61));

        inbox.HasLiveSubscriber(TimeSpan.FromSeconds(60)).ShouldBeFalse();

        // ...while the subscriber itself is very much still there: the buffered item is delivered
        // on the next poll. "Not live" must not be read as "evicted" — the two answers are
        // deliberately different, and this is the case that separates them.
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveAsync_WhenAnotherPollTakesTheBatchFirst_ReturnsEmptyWithoutLosingItems()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() =>
            inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None));

        inbox.Enqueue(Message("c1"));

        // The parked poll has been signalled but cannot resume until pumped, so the second poll
        // takes the batch. The item must land in exactly one of them, never be lost between them.
        var taken = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        taken.Count.ShouldBe(1);
        taken[0].Message!.ConversationId.ShouldBe("c1");
        (await context.PumpUntilAsync(parked, Deadline)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_WhenCancelled_LeavesItemsForTheNextPoll()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() => inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), cts.Token));

        inbox.Enqueue(Message("c1"));
        await cts.CancelAsync();

        // The caller is gone, so the batch must stay queued rather than be handed to an aborted poll.
        await Should.ThrowAsync<OperationCanceledException>(() => context.PumpUntilAsync(parked, Deadline));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    // The sibling of ReceiveAsync_WhenCancelled_LeavesItemsForTheNextPoll, on the path that has no
    // wait at all. ReconnectAsync aborts a poll and starts a fresh one immediately behind it, so a
    // token that is already cancelled when the poll arrives is the common case, not a corner: the
    // pending-items fast path must refuse it exactly like the wait path does, or the batch is
    // drained into a caller that is already gone.
    [Fact]
    public async Task ReceiveAsync_WhenAlreadyCancelled_LeavesPendingItemsQueued()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), cts.Token));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_WhenAResumingPollFinishes_DoesNotOrphanALaterWaiter()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() =>
            inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None));

        inbox.Enqueue(Message("c1"));

        // Take everything the parked poll was signalled for, then register a fresh waiter while it
        // is still suspended. Retiring the parked poll must not null out this later waiter.
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
        var next = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        (await context.PumpUntilAsync(parked, Deadline)).ShouldBeEmpty();

        inbox.Enqueue(Message("c2"));

        var batch = await next.WaitAsync(Deadline);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c2");
    }

    // Parks a poll's continuations on a context only the test pumps, which pins the interleaving
    // the racy sites need without adding a seam to production code. It works because the inbox
    // awaits without ConfigureAwait(false), so the continuation is posted to the captured context;
    // adding ConfigureAwait(false) there would not fail these tests, it would quietly make them
    // race again.
    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public T Start<T>(Func<T> work)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return work();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task<T> PumpUntilAsync<T>(Task<T> task, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!task.IsCompleted)
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("The parked poll never completed.");
                }

                RunPending();
                await Task.Delay(5);
            }

            return await task;
        }

        private void RunPending()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_pending.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}