using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

// This channel's own payload shape, and nothing else. Liveness and buffering are the shared
// emitter's business and are pinned once at the policy seam
// (Tests/Unit/Channels.Hosting/DeliveryPolicyTests.cs).
public class ChannelNotificationEmitterTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "servicebus";

    [Fact]
    public async Task EmitAsync_CarriesTheServiceBusPromptFields()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);
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
}