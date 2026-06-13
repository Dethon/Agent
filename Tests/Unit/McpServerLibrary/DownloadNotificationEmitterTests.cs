using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class DownloadNotificationEmitterTests
{
    [Fact]
    public void HasActiveSessions_OnlyToolClientSessions_IsFalse()
    {
        var emitter = Emitter();
        emitter.RegisterSession("tool", Session("Jack").Object);

        emitter.HasActiveSessions.ShouldBeFalse();
    }

    [Fact]
    public void HasActiveSessions_ChannelClientSession_IsTrue()
    {
        var emitter = Emitter();
        emitter.RegisterSession("channel", Session("channel-library").Object);

        emitter.HasActiveSessions.ShouldBeTrue();
    }

    [Fact]
    public async Task EmitAsync_OnlyToolClientSessions_ReturnsFalse()
    {
        var emitter = Emitter();
        emitter.RegisterSession("tool", Session("Jack").Object);

        var delivered = await emitter.EmitAsync(Payload());

        delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task EmitAsync_ChannelClientSession_DeliversAndReturnsTrue()
    {
        var emitter = Emitter();
        emitter.RegisterSession("channel", Session("channel-library").Object);

        var delivered = await emitter.EmitAsync(Payload());

        delivered.ShouldBeTrue();
    }

    [Fact]
    public async Task EmitAsync_MixedSessions_SendsOnlyToChannelClients()
    {
        var emitter = Emitter();
        var tool = Session("Jack");
        var channel = Session("channel-library");
        emitter.RegisterSession("tool", tool.Object);
        emitter.RegisterSession("channel", channel.Object);

        var delivered = await emitter.EmitAsync(Payload());

        delivered.ShouldBeTrue();
        tool.Verify(
            s => s.SendMessageAsync(It.IsAny<JsonRpcMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(
            s => s.SendMessageAsync(It.IsAny<JsonRpcMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static DownloadNotificationEmitter Emitter() =>
        new(NullLogger<DownloadNotificationEmitter>.Instance);

    private static Mock<McpServer> Session(string clientName)
    {
        var server = new Mock<McpServer>();
        server.SetupGet(s => s.ClientInfo).Returns(new Implementation { Name = clientName, Version = "1.0.0" });
        return server;
    }

    private static ChannelMessageNotification Payload() => new()
    {
        ConversationId = "conv-1",
        Sender = "fran",
        Content = "[download-complete] done",
        Timestamp = DateTimeOffset.UtcNow
    };
}