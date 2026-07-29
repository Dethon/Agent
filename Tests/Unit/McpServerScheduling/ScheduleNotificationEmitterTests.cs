using Domain.Channels;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationEmitterTests
{
    [Fact]
    public async Task EmitAsync_WithASubscriber_EnqueuesMessageItemAndReturnsTrue()
    {
        var inbox = new ChannelInbox();
        var sut = new ScheduleNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-scheduling", TimeSpan.Zero, CancellationToken.None);

        var delivered = await sut.EmitAsync(Payload());

        delivered.ShouldBeTrue();
        var items = await inbox.ReceiveAsync("channel-scheduling", TimeSpan.Zero, CancellationToken.None);
        items.Count.ShouldBe(1);
        items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
        items[0].Message!.ConversationId.ShouldBe("sched-1");
        items[0].Message!.Sender.ShouldBe("scheduler");
        items[0].Message!.Content.ShouldBe("run it");
    }

    [Fact]
    public async Task EmitAsync_NoSubscribers_ReturnsFalseWithoutEnqueuing()
    {
        var inbox = new ChannelInbox();
        var sut = new ScheduleNotificationEmitter(inbox);

        var delivered = await sut.EmitAsync(Payload());

        delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task HasActiveSessions_FollowsInboxSubscribers()
    {
        var inbox = new ChannelInbox();
        var sut = new ScheduleNotificationEmitter(inbox);

        sut.HasActiveSessions.ShouldBeFalse();

        await inbox.ReceiveAsync("channel-scheduling", TimeSpan.Zero, CancellationToken.None);

        sut.HasActiveSessions.ShouldBeTrue();
    }

    // The regression this test pins: PruneIdle keeps a subscriber that is holding items alive for
    // up to an hour so a channel outage survives (see ChannelInboxTests), but a stale buffered
    // subscriber must not be mistaken for "someone is listening" here — ScheduleDispatcherService
    // deletes/advances a due schedule only when EmitAsync reports true, so a false-true here would
    // destroy the durable record in exchange for an in-memory buffer that dies on the next restart.
    [Fact]
    public async Task EmitAsync_SubscriberWentStaleWhileStillBufferingAnItem_ReturnsFalse()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        var sut = new ScheduleNotificationEmitter(inbox);
        await inbox.ReceiveAsync("channel-scheduling", TimeSpan.Zero, CancellationToken.None);

        (await sut.EmitAsync(Payload())).ShouldBeTrue();

        // The agent's channel connection drops and never repolls; the subscriber keeps buffering
        // (nothing evicts it — it still holds the item above), but nobody is actually listening.
        time.Advance(TimeSpan.FromMinutes(2));

        sut.HasActiveSessions.ShouldBeFalse();
        (await sut.EmitAsync(Payload())).ShouldBeFalse();
    }

    [Fact]
    public void Emitter_IsConstructibleFromTheRegisteredInbox()
    {
        var provider = new ServiceCollection()
            .AddSingleton<ChannelInbox>()
            .AddSingleton<ScheduleNotificationEmitter>()
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<ScheduleNotificationEmitter>());
    }

    private static ChannelMessageNotification Payload() => new()
    {
        ConversationId = "sched-1",
        Sender = "scheduler",
        Content = "run it",
        Timestamp = DateTimeOffset.UtcNow
    };
}