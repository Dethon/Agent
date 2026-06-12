using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorAnnounceTests
{
    private static (Mock<IChannelConnection> Mock, List<(string? InitialPrompt, string? ExistingConversationId)> Calls) Channel(string id)
    {
        var calls = new List<(string?, string?)>();
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? prompt, string? _, string? existing, CancellationToken _) => calls.Add((prompt, existing)))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing);
        return (m, calls);
    }

    private static ChannelMessage DownloadMessage(string content = "[download-complete] film.mkv")
    {
        return new ChannelMessage
        {
            ConversationId = "7:42",
            Content = content,
            Sender = "fran",
            ChannelId = "library",
            AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null)
        };
    }

    [Fact]
    public async Task AnnounceTurnStart_PreExistingTarget_CallsCreateConversationWithExistingIdAndPrompt()
    {
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "7:42") };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);

        var call = calls.ShouldHaveSingleItem();
        call.ExistingConversationId.ShouldBe("7:42");
        call.InitialPrompt.ShouldBe("[download-complete] film.mkv");
    }

    [Fact]
    public async Task AnnounceTurnStart_MintedTarget_SkippedWhenSkipMintedIsTrue()
    {
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "minted-1", Minted: true) };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnnounceTurnStart_MintedTarget_AnnouncedWhenSkipMintedIsFalse()
    {
        // A later message reusing the group-level targets sees conversations that were
        // minted by the FIRST message's resolution — for this turn they pre-exist.
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "minted-1", Minted: true) };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: false, CancellationToken.None);

        calls.ShouldHaveSingleItem().ExistingConversationId.ShouldBe("minted-1");
    }

    [Fact]
    public async Task AnnounceTurnStart_VoiceTarget_AlwaysSkipped()
    {
        var (voice, calls) = Channel("voice");
        var targets = new[] { new ChatMonitor.DeliveryTarget(voice.Object, "7:42") };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: false, CancellationToken.None);

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnnounceTurnStart_AnnounceThrows_SwallowsAndContinuesToNextTarget()
    {
        var failing = new Mock<IChannelConnection>();
        failing.SetupGet(c => c.ChannelId).Returns("signalr");
        failing.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var (telegram, telegramCalls) = Channel("telegram");
        var targets = new[]
        {
            new ChatMonitor.DeliveryTarget(failing.Object, "7:42"),
            new ChatMonitor.DeliveryTarget(telegram.Object, "t-9")
        };

        await Should.NotThrowAsync(
            ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None));

        telegramCalls.ShouldHaveSingleItem();
    }
}