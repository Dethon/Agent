using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// This channel's own payload shape: the room location, satellite id and dismissed-alert marker it
// is the only channel to populate. Two of them are adjacent optional strings, which is why they
// ride the shared payload as named properties. Liveness and buffering are pinned once at the
// policy seam (Tests/Unit/Channels.Hosting/DeliveryPolicyTests.cs).
public class ChannelNotificationEmitterTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "voice";

    [Fact]
    public async Task EmitAsync_CarriesTheVoiceSpecificFields()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.Broadcast);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "fran",
            Content = "que hora es",
            AgentId = "nabu",
            Location = "Kitchen (Madrid, Spain)",
            SatelliteId = "kitchen-01",
            DismissedAlert = "alarm \"Take out the trash\"",
            Timestamp = DateTimeOffset.UtcNow
        });

        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("fran");
        items[0].Message!.Content.ShouldBe("que hora es");
        items[0].Message!.Location.ShouldBe("Kitchen (Madrid, Spain)");
        items[0].Message!.SatelliteId.ShouldBe("kitchen-01");
        items[0].Message!.DismissedAlert.ShouldBe("alarm \"Take out the trash\"");
    }
}