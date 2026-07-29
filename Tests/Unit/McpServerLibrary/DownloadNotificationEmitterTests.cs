using Domain.Channels;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class DownloadNotificationEmitterTests
{
    [Fact]
    public async Task EmitAsync_WithASubscriber_EnqueuesMessageItemAndReturnsTrue()
    {
        var inbox = new ChannelInbox();
        var sut = new DownloadNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-library", TimeSpan.Zero, CancellationToken.None);

        var delivered = await sut.EmitAsync(Payload());

        delivered.ShouldBeTrue();
        var items = await inbox.ReceiveAsync("channel-library", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("conv-1");
        items[0].Message!.Sender.ShouldBe("fran");
        items[0].Message!.Content.ShouldBe("[download-complete] done");
    }

    [Fact]
    public async Task EmitAsync_NoSubscribers_ReturnsFalseWithoutEnqueuing()
    {
        var inbox = new ChannelInbox();
        var sut = new DownloadNotificationEmitter(inbox);

        var delivered = await sut.EmitAsync(Payload());

        delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task HasActiveSessions_FollowsInboxSubscribers()
    {
        var inbox = new ChannelInbox();
        var sut = new DownloadNotificationEmitter(inbox);

        sut.HasActiveSessions.ShouldBeFalse();

        await inbox.ReceiveAsync("channel-library", TimeSpan.Zero, CancellationToken.None);

        sut.HasActiveSessions.ShouldBeTrue();
    }

    [Fact]
    public void Emitter_IsConstructibleFromTheRegisteredInbox()
    {
        var provider = new ServiceCollection()
            .AddSingleton<ChannelInbox>()
            .AddSingleton<DownloadNotificationEmitter>()
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<DownloadNotificationEmitter>());
    }

    private static ChannelMessageNotification Payload() => new()
    {
        ConversationId = "conv-1",
        Sender = "fran",
        Content = "[download-complete] done",
        Timestamp = DateTimeOffset.UtcNow
    };
}