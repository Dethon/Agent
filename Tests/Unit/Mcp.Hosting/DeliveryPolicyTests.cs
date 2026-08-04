using Domain.Channels;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// The policy seam: the emitter and a real ChannelInbox together, asserted on what a subscriber
// ends up holding and on what the emit reports back. The three policies differ only in the
// no-live-subscriber case, which is exactly the case that regressed three times across six
// hand-copied emitters, so every policy is pinned here rather than once per server.
public class DeliveryPolicyTests
{
    private const string Subscriber = "channel-test";

    private static ChannelMessageNotification Message(string conversationId = "c1") => new()
    {
        ConversationId = conversationId,
        Sender = "user",
        Content = "hello"
    };

    private static async Task<IReadOnlyList<ChannelInboxItem>> DrainAsync(ChannelInbox inbox) =>
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

    // Registers the subscriber the way production does — by polling — so it counts as live.
    private static Task RegisterAsync(ChannelInbox inbox) => DrainAsync(inbox);

    [Fact]
    public async Task EmitAsync_Broadcast_ReachesASubscriberThatIsIdleButNotYetPruned()
    {
        var clock = new FakeTimeProvider();
        var inbox = new ChannelInbox(clock);
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.Broadcast);

        // Past the freshness window, so nobody is "live", but far short of the hour that prunes an
        // empty subscriber. Broadcast delivers anyway: a brief agent gap must not lose the item.
        clock.Advance(TimeSpan.FromMinutes(5));

        var live = await sut.EmitAsync(Message());

        live.ShouldBeFalse();
        var batch = await DrainAsync(inbox);
        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task EmitAsync_GateOnLive_WithNoLiveSubscriber_BuffersNothing()
    {
        var clock = new FakeTimeProvider();
        var inbox = new ChannelInbox(clock);
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);

        clock.Advance(TimeSpan.FromMinutes(5));

        var live = await sut.EmitAsync(Message());

        live.ShouldBeFalse();
        (await DrainAsync(inbox)).ShouldBeEmpty();
    }

    [Fact]
    public async Task EmitAsync_BufferAlways_WithNoSubscriberYet_CreatesTheQueueOnDemand()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.BufferAlways, Subscriber);

        var live = await sut.EmitAsync(Message());

        live.ShouldBeFalse();
        var batch = await DrainAsync(inbox);
        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task EmitAsync_Broadcast_WithNoSubscriberAtAll_FansOutToNobody()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.Broadcast);

        var live = await sut.EmitAsync(Message());

        live.ShouldBeFalse();
        (await DrainAsync(inbox)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DeliveryPolicy.Broadcast, null)]
    [InlineData(DeliveryPolicy.GateOnLive, null)]
    [InlineData(DeliveryPolicy.BufferAlways, Subscriber)]
    public async Task EmitAsync_WithALiveSubscriber_DeliversAndReportsLive(
        DeliveryPolicy policy, string? subscriberId)
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, policy, subscriberId);

        var live = await sut.EmitAsync(Message());

        live.ShouldBeTrue();
        (await DrainAsync(inbox)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(DeliveryPolicy.Broadcast, null)]
    [InlineData(DeliveryPolicy.GateOnLive, null)]
    [InlineData(DeliveryPolicy.BufferAlways, Subscriber)]
    public async Task EmitCancelAsync_FollowsTheSamePolicyAsMessages(
        DeliveryPolicy policy, string? subscriberId)
    {
        var clock = new FakeTimeProvider();
        var inbox = new ChannelInbox(clock);
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, policy, subscriberId);

        clock.Advance(TimeSpan.FromMinutes(5));

        var live = await sut.EmitCancelAsync(new ChannelCancelNotification { ConversationId = "c1" });

        live.ShouldBeFalse();
        var expected = policy == DeliveryPolicy.GateOnLive ? 0 : 1;
        (await DrainAsync(inbox)).Count.ShouldBe(expected);
    }

    // The freshness question is answered inside the emit and reported back, so a caller cannot
    // read "a subscriber exists" as "someone is listening" — the near-miss that produced the same
    // production defect three separate times.
    [Fact]
    public async Task EmitAsync_ASubscriberHoldingBufferedItemsButNotRepolling_IsNotLive()
    {
        var clock = new FakeTimeProvider();
        var inbox = new ChannelInbox(clock);
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.Broadcast);

        clock.Advance(TimeSpan.FromMinutes(5));
        await sut.EmitAsync(Message("buffered"));

        (await sut.EmitAsync(Message("second"))).ShouldBeFalse();
    }

    [Fact]
    public void Constructor_BufferAlways_WithoutASubscriberId_Throws() =>
        Should.Throw<ArgumentException>(() =>
            new ChannelNotificationEmitter(new ChannelInbox(), DeliveryPolicy.BufferAlways));

    [Theory]
    [InlineData(DeliveryPolicy.Broadcast)]
    [InlineData(DeliveryPolicy.GateOnLive)]
    public void Constructor_NonBufferingPolicy_WithASubscriberId_Throws(DeliveryPolicy policy) =>
        Should.Throw<ArgumentException>(() =>
            new ChannelNotificationEmitter(new ChannelInbox(), policy, Subscriber));
}