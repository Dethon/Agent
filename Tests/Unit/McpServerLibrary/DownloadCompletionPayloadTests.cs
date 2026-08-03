using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

// This server's own payload shape. The gate-on-live behaviour it depends on is pinned once at the
// policy seam (Tests/Unit/Channels.Hosting/DeliveryPolicyTests.cs), and what the watcher does with
// a false return is pinned in DownloadCompletionWatcherTests.
public class DownloadCompletionPayloadTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "library";

    [Fact]
    public async Task EmitAsync_CarriesTheDownloadCompletionFields()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        var delivered = await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "fran",
            Content = "[download-complete] done",
            AgentId = "jack",
            ReplyTo = [new ReplyTarget("signalr", "conv-1")],
            Timestamp = DateTimeOffset.UtcNow
        });

        delivered.ShouldBeTrue();
        var items = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("fran");
        items[0].Message!.Content.ShouldBe("[download-complete] done");
        items[0].Message!.ReplyTo!.Single().ChannelId.ShouldBe("signalr");
    }

    // Only the agent's channel connection ever long-polls this dual-role server; its per-conversation
    // tool sessions never poll, so nothing here filters by client name and the distinction stays
    // structural. A tool session that never polls simply never counts as live.
    [Fact]
    public async Task EmitAsync_WithOnlyToolSessionsAndNoPoller_ReportsNobodyListening()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox, DeliveryPolicy.GateOnLive);

        (await sut.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "fran",
            Content = "[download-complete] done"
        })).ShouldBeFalse();

        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None)).ShouldBeEmpty();
    }
}