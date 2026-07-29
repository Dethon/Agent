using Domain.Channels;
using McpChannelVoice.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public async Task EmitMessageNotificationAsync_EnqueuesMessageItemForPollingSubscriber()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-voice", TimeSpan.Zero, CancellationToken.None);

        await sut.EmitMessageNotificationAsync(
            "conv-1", "user", "hola", "nabu", "Kitchen", "kitchen-01", "alarm \"Take out the trash\"");

        var items = await inbox.ReceiveAsync("channel-voice", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("user");
        items[0].Message!.Content.ShouldBe("hola");
        items[0].Message!.AgentId.ShouldBe("nabu");
        items[0].Message!.Location.ShouldBe("Kitchen");
        items[0].Message!.SatelliteId.ShouldBe("kitchen-01");
        items[0].Message!.DismissedAlert.ShouldBe("alarm \"Take out the trash\"");
    }

    [Fact]
    public async Task HasActiveSessions_FollowsInboxSubscribers()
    {
        var inbox = new ChannelInbox();
        var sut = new ChannelNotificationEmitter(inbox);

        sut.HasActiveSessions.ShouldBeFalse();

        await inbox.ReceiveAsync("channel-voice", TimeSpan.Zero, CancellationToken.None);

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