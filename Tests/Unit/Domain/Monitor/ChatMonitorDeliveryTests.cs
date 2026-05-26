using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Moq;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorDeliveryTests
{
    private static IChannelConnection Channel(string id)
    {
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, CancellationToken _) => $"minted-{id}");
        return m.Object;
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithoutReplyTo_DeliversToOrigin()
    {
        var origin = Channel("signalr");
        var channels = new[] { origin, Channel("telegram") };
        var msg = new ChannelMessage { ConversationId = "c1", Content = "x", Sender = "u", ChannelId = "signalr" };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        var t = targets.ShouldHaveSingleItem();
        t.Channel.ChannelId.ShouldBe("signalr");
        t.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithReplyTo_FansOutAndMintsMissingConversations()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr"), Channel("telegram") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "x",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-9")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        targets.Count.ShouldBe(2);
        targets[0].ConversationId.ShouldBe("minted-signalr");
        targets[1].ConversationId.ShouldBe("t-9");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WithUnknownChannelInReplyTo_SkipsIt()
    {
        var origin = Channel("scheduling");
        var channels = new[] { origin, Channel("signalr") };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("does-not-exist", "z")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WhenMintingConversation_PassesMessageContentAsInitialPrompt()
    {
        // A scheduled fire delivers to WebChat with a null ReplyTo conversationId,
        // so a new conversation is minted. The minted channel must receive the
        // schedule's actual prompt text as `initialPrompt`, otherwise WebChat
        // displays the topic name ("Scheduled task") in place of the user bubble.
        var origin = Channel("scheduling");
        var captor = new Mock<IChannelConnection>();
        captor.SetupGet(c => c.ChannelId).Returns("signalr");
        string? capturedPrompt = null;
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? p, CancellationToken _) => capturedPrompt = p)
            .ReturnsAsync("minted-signalr");
        var channels = new[] { origin, captor.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Check qBittorrent for stalled torrents",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null)]
        };

        await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        capturedPrompt.ShouldBe("Check qBittorrent for stalled torrents");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_WhenMintingThrows_SkipsTargetInsteadOfThrowing()
    {
        var origin = Channel("scheduling");
        var failing = new Mock<IChannelConnection>();
        failing.SetupGet(c => c.ChannelId).Returns("signalr");
        failing.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var channels = new[] { origin, failing.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1", Content = "x", Sender = "s", ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("signalr", null)]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        targets.ShouldBeEmpty();
    }
}