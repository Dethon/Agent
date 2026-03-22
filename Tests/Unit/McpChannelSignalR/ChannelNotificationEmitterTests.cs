using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class ChannelNotificationEmitterTests
{
    private readonly ChannelNotificationEmitter _sut = new(
        new Mock<ILogger<ChannelNotificationEmitter>>().Object);

    [Fact]
    public void HasActiveSessions_Initially_ReturnsFalse()
    {
        _sut.HasActiveSessions.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterSession_RemovesSession()
    {
        _sut.RegisterSession("sess-1", null!);

        _sut.HasActiveSessions.ShouldBeTrue();
        _sut.UnregisterSession("sess-1");
        _sut.HasActiveSessions.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterSession_UnknownId_DoesNotThrow()
    {
        Should.NotThrow(() => _sut.UnregisterSession("nonexistent"));
    }

    [Fact]
    public async Task EmitMessageNotificationAsync_NoSessions_CompletesWithoutError()
    {
        await Should.NotThrowAsync(() =>
            _sut.EmitMessageNotificationAsync("conv-1", "user", "hi", "agent1"));
    }
}
