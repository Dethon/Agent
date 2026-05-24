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
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string a, string t, string s, CancellationToken _) => $"minted-{id}");
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
}