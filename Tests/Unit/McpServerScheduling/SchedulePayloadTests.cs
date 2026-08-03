using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpServerScheduling;

// This server's own payload shape. The gate-on-live behaviour it depends on is pinned once at the
// policy seam (Tests/Unit/Channels.Hosting/DeliveryPolicyTests.cs), and what the dispatcher does
// with a false return is pinned in ScheduleDispatcherServiceTests.
public class SchedulePayloadTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "scheduling";

    [Fact]
    public async Task EmitAsync_CarriesTheSchedulePayloadFields()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        var delivered = await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "sched-1",
            Sender = "scheduler",
            Content = "run it",
            AgentId = "jack",
            ReplyTo = [new ReplyTarget("signalr", "conv-1")],
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "sched-1"),
            Timestamp = DateTimeOffset.UtcNow
        });

        delivered.ShouldBeTrue();
        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("sched-1");
        items[0].Message!.Sender.ShouldBe("scheduler");
        items[0].Message!.Content.ShouldBe("run it");
        items[0].Message!.ReplyTo!.Single().ChannelId.ShouldBe("signalr");
        items[0].Message!.Origin!.Kind.ShouldBe(MessageOriginKind.Schedule);
    }
}