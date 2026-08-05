using Domain.Channels;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// This channel's own payload shape, and the field only it populates: the per-message config patch.
// Liveness and buffering are the shared emitter's business and are pinned once at the policy seam
// (Tests/Unit/Mcp.Hosting/DeliveryPolicyTests.cs).
public class ChannelNotificationEmitterTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "signalr";

    private static ChannelNotificationEmitter Emitter(ChannelInbox inbox) =>
        new(inbox, DeliveryPolicy.Broadcast);

    // The one transport-specific field on this channel. It rides the shared payload as a named
    // property, so carrying it no longer widens anybody's parameter list.
    [Fact]
    public async Task EmitAsync_WithConfigPatch_PutsPatchOnNotification()
    {
        var inbox = new ChannelInbox();
        var sut = Emitter(inbox);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "chat:thread",
            Sender = "fran",
            Content = "hello",
            AgentId = "jack",
            ConfigPatch = new AgentConfigPatch { Model = "z-ai/glm-5.2" }
        });

        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Message!.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }
}