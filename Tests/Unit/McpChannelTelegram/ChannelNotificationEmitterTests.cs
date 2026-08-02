using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

// This channel's own payload shape, and the one thing that is specific to it: the buffer-always
// target. Liveness is the shared emitter's business and is pinned once at the policy seam
// (Tests/Unit/Channels.Hosting/DeliveryPolicyTests.cs).
public class ChannelNotificationEmitterTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "telegram";

    private static ChannelNotificationEmitter Emitter(ChannelInbox inbox) =>
        new(inbox, DeliveryPolicy.BufferAlways, Subscriber);

    [Fact]
    public async Task EmitAsync_CarriesTheTelegramMessageFields()
    {
        var inbox = new ChannelInbox();
        var sut = Emitter(inbox);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "user",
            Content = "hola",
            AgentId = "nabu",
            Timestamp = DateTimeOffset.UtcNow
        });

        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("user");
        items[0].Message!.Content.ShouldBe("hola");
        items[0].Message!.AgentId.ShouldBe("nabu");
    }

    // The cold-start window: a message arriving after a server restart but before the agent's
    // first poll used to fan out to nobody and vanish while the service logged "buffering".
    // Targeting the well-known subscriber id creates the queue on demand, so it buffers for real.
    [Fact]
    public async Task EmitAsync_BeforeAnySubscriberRegistered_StillBuffers()
    {
        var inbox = new ChannelInbox();

        await Emitter(inbox).EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "user",
            Content = "hola"
        });

        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Message!.Content.ShouldBe("hola");
    }
}