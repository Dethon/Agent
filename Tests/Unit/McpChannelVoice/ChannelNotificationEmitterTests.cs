using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public void UnregisterSession_OnUnknownId_DoesNotThrow()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        Should.NotThrow(() => emitter.UnregisterSession("nope"));
    }
}