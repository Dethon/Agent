using Domain.Channels;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public async Task EmitMessageNotificationAsync_EnqueuesMessageItemForPollingSubscriber()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

        await sut.EmitMessageNotificationAsync("conv-1", "user", "hola", "nabu");

        var items = await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("user");
        items[0].Message!.Content.ShouldBe("hola");
        items[0].Message!.AgentId.ShouldBe("nabu");
    }

    [Fact]
    public async Task EmitMessageNotificationAsync_WithConfigPatch_PutsPatchOnNotification()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

        await sut.EmitMessageNotificationAsync(
            "chat:thread", "fran", "hello", "jack",
            new AgentConfigPatch { Model = "z-ai/glm-5.2" });

        var items = await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Message!.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public async Task EmitCancelNotificationAsync_EnqueuesCancelItemForPollingSubscriber()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

        await sut.EmitCancelNotificationAsync("conv-1", "nabu");

        var items = await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Cancel);
        items[0].Cancel!.ConversationId.ShouldBe("conv-1");
        items[0].Cancel!.AgentId.ShouldBe("nabu");
    }

    [Fact]
    public async Task HasActiveSessions_FollowsInboxSubscribers()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);

        sut.HasActiveSessions.ShouldBeFalse();

        await inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

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