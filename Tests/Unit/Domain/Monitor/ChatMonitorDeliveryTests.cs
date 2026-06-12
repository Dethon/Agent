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
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? _, CancellationToken _) => $"minted-{id}");
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
    public async Task ResolveDeliveryTargets_DownloadCompletion_UsesConcreteConversationWithoutMinting()
    {
        var origin = Channel("library");
        var signalr = new FakeChannelConnection { ChannelId = "signalr" };
        var msg = new ChannelMessage
        {
            ConversationId = "conv-7", Content = "[download-complete] ...", Sender = "fran",
            ChannelId = "library", AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            ReplyTo = [new ReplyTarget("signalr", "conv-7")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin, signalr], CancellationToken.None);

        var target = targets.ShouldHaveSingleItem();
        target.ConversationId.ShouldBe("conv-7");
        target.Channel.ChannelId.ShouldBe("signalr");
        signalr.CreatedConversations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveDeliveryTargets_VoiceOriginDownload_KeepsConcreteConversationAndAddress()
    {
        var origin = Channel("library");
        var voice = new FakeChannelConnection { ChannelId = "voice" };
        var msg = new ChannelMessage
        {
            ConversationId = "conv-9", Content = "[download-complete] ...", Sender = "fran",
            ChannelId = "library", AgentId = "jack",
            ReplyTo = [new ReplyTarget("voice", "conv-9", "fran-office-01")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin, voice], CancellationToken.None);

        targets.ShouldHaveSingleItem().ConversationId.ShouldBe("conv-9");
        voice.CreatedConversations.ShouldBeEmpty();
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? p, string? _, string? _, CancellationToken _) => capturedPrompt = p)
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task ResolveDeliveryTargets_ThreadsReplyTargetAddressIntoCreateConversation()
    {
        var origin = Channel("scheduling");
        var captor = new Mock<IChannelConnection>();
        captor.SetupGet(c => c.ChannelId).Returns("voice");
        string? capturedAddress = null;
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? _, string? a, string? _, CancellationToken _) => capturedAddress = a)
            .ReturnsAsync("minted-voice");
        var channels = new[] { origin, captor.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "The AC is on",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "mycroft",
            ReplyTo = [new ReplyTarget("voice", null, "fran-office-01")]
        };

        await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        capturedAddress.ShouldBe("fran-office-01");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_SignalrThenVoice_AttachesVoiceToSignalrConversation()
    {
        // A schedule delivering to both signalr and voice must produce ONE shared
        // conversation: signalr mints it; voice attaches to that same id (so WebChat shows
        // a single populated thread and the satellite speaks it). The first minted target's
        // id is threaded into later targets as `existingConversationId`.
        var origin = Channel("scheduling");
        var signalr = Channel("signalr");
        var voice = new Mock<IChannelConnection>();
        voice.SetupGet(c => c.ChannelId).Returns("voice");
        string? voiceExistingId = null;
        voice.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => voiceExistingId = existing)
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing ?? "minted-voice");
        var channels = new[] { origin, signalr, voice.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Avisa a Fran",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "mycroft",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("voice", null, "fran-office-01")]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        voiceExistingId.ShouldBe("minted-signalr");
        targets.Count.ShouldBe(2);
        targets[0].ConversationId.ShouldBe("minted-signalr");
        targets[1].ConversationId.ShouldBe("minted-signalr");
    }

    [Fact]
    public async Task ResolveDeliveryTargets_VoiceListedBeforeSignalr_StillAnchorsSharedIdOnSignalr()
    {
        // Voice can only ATTACH to an existing conversation (it has no persisted TopicId to hand
        // back), so a topic-owning channel must anchor the shared id and become targets[0] (the
        // chat-history persistence + approval anchor). Even when the schedule lists voice FIRST,
        // resolution must order voice last so signalr mints the shared id and voice attaches to it.
        // Otherwise voice anchors a id signalr ignores -> two divergent ids -> empty WebChat thread.
        var origin = Channel("scheduling");
        var signalr = Channel("signalr");
        var voice = new Mock<IChannelConnection>();
        voice.SetupGet(c => c.ChannelId).Returns("voice");
        voice.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing ?? "minted-voice");
        var channels = new[] { origin, signalr, voice.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Avisa a Fran",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "mycroft",
            ReplyTo = [new ReplyTarget("voice", null, "fran-office-01"), new ReplyTarget("signalr", null)]
        };

        var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        targets.Count.ShouldBe(2);
        // signalr (owning) anchors and is targets[0]; voice attaches to the same id.
        targets[0].Channel.ChannelId.ShouldBe("signalr");
        targets[0].ConversationId.ShouldBe("minted-signalr");
        targets.ShouldAllBe(t => t.ConversationId == "minted-signalr");
    }
}