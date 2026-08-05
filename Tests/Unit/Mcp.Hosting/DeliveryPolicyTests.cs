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

    // Buffer-always targets one subscriber id, so its liveness answer must be about that id. "Is
    // anyone live" would read an agent polling under a different derived id as delivery, and the
    // only diagnostic — the caller's not-live warning — would never fire while items pile into a
    // queue nobody drains: exactly the silent failure DeliveryPolicyRules warns about.
    [Fact]
    public async Task EmitAsync_BufferAlways_WithOnlyADifferentIdLive_BuffersAndReportsNotLive()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync("channel-other", TimeSpan.Zero, CancellationToken.None);
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.BufferAlways, Subscriber);

        var live = await sut.EmitAsync(Message());

        live.ShouldBeFalse();
        (await DrainAsync(inbox)).Count.ShouldBe(1);
    }

    // Three callers pass a real token and act on the answer: a schedule is deleted or stamped, a
    // download routing entry is removed, a broker message is completed. An emit that ignored the
    // token could hand a settled record back on the way down while the item it settled for never
    // reached anyone, so a cancelled emit delivers nothing and says so by throwing.
    [Theory]
    [InlineData(DeliveryPolicy.Broadcast, null)]
    [InlineData(DeliveryPolicy.GateOnLive, null)]
    [InlineData(DeliveryPolicy.BufferAlways, Subscriber)]
    public async Task EmitAsync_WithACancelledToken_DeliversNothingAndThrows(
        DeliveryPolicy policy, string? subscriberId)
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, policy, subscriberId);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => sut.EmitAsync(Message(), cts.Token));

        (await DrainAsync(inbox)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DeliveryPolicy.Broadcast, null)]
    [InlineData(DeliveryPolicy.GateOnLive, null)]
    [InlineData(DeliveryPolicy.BufferAlways, Subscriber)]
    public async Task EmitCancelAsync_WithACancelledToken_DeliversNothingAndThrows(
        DeliveryPolicy policy, string? subscriberId)
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, policy, subscriberId);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.EmitCancelAsync(new ChannelCancelNotification { ConversationId = "c1" }, cts.Token));

        (await DrainAsync(inbox)).ShouldBeEmpty();
    }

    // Gate-on-live is the policy whose callers settle a durable record on the answer, so the answer
    // has to be about the item, not about a moment before it. A clock that moves between reads is
    // what a subscriber going quiet mid-emit looks like from inside: the liveness question sees a
    // subscriber and the enqueue that follows finds it pruned. Asking and enqueueing as one
    // operation is the only way the two can agree.
    [Fact]
    public async Task EmitAsync_GateOnLive_WhenTheSubscriberGoesAwayMidEmit_ReportsWhatTheItemGot()
    {
        var clock = new SteppingTimeProvider(TimeSpan.FromSeconds(6));
        var inbox = new ChannelInbox(clock, subscriberIdleTimeout: TimeSpan.FromSeconds(10));
        await RegisterAsync(inbox);
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);

        var live = await sut.EmitAsync(Message());

        var delivered = (await DrainAsync(inbox)).Count == 1;
        live.ShouldBe(delivered);
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

    // Time moves on every read, so any two reads inside one operation see a different clock. That
    // makes a check that merely precedes an action fail deterministically, where a fixed clock
    // would hide it behind a race no test can lose on purpose.
    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now = now + step;
            return now;
        }
    }
}