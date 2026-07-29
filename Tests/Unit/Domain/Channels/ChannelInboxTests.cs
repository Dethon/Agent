using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class ChannelInboxTests
{
    private const string Subscriber = "channel-signalr";

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

    [Fact]
    public async Task Subscriber_IsEvictedAfterIdleTimeout()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.HasSubscribers.ShouldBeTrue();

        time.Advance(TimeSpan.FromMinutes(6));

        inbox.HasSubscribers.ShouldBeFalse();
    }
}