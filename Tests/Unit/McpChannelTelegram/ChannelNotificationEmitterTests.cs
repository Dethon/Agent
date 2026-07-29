using Domain.Channels;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public async Task EmitMessageNotificationAsync_EnqueuesMessageItemForPollingSubscriber()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-telegram", TimeSpan.Zero, CancellationToken.None);

        await sut.EmitMessageNotificationAsync("conv-1", "user", "hola", "nabu");

        var items = await inbox.ReceiveAsync("channel-telegram", TimeSpan.Zero, CancellationToken.None);
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
    public async Task EmitMessageNotificationAsync_BeforeAnySubscriberRegistered_StillBuffers()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);

        await sut.EmitMessageNotificationAsync("conv-1", "user", "hola", "nabu");

        var items = await inbox.ReceiveAsync("channel-telegram", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Message!.Content.ShouldBe("hola");
    }

    [Fact]
    public async Task HasActiveSessions_FollowsInboxSubscribers()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);

        sut.HasActiveSessions.ShouldBeFalse();

        await inbox.ReceiveAsync("channel-telegram", TimeSpan.Zero, CancellationToken.None);

        sut.HasActiveSessions.ShouldBeTrue();
    }

    [Fact]
    public void Emitter_IsConstructibleFromTheRegisteredInbox()
    {
        var provider = new ServiceCollection()
            .AddSingleton<ChannelInbox>()
            .AddSingleton<ChannelNotificationEmitter>()
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<ChannelNotificationEmitter>());
    }
}