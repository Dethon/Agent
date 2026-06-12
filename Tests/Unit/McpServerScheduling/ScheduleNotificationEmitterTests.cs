using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationEmitterTests
{
    [Fact]
    public void HasActiveSessions_OnlyToolClientSessions_IsFalse()
    {
        var emitter = Emitter();
        emitter.RegisterSession("tool", Session("Jack"));

        emitter.HasActiveSessions.ShouldBeFalse();
    }

    [Fact]
    public void HasActiveSessions_ChannelClientSession_IsTrue()
    {
        var emitter = Emitter();
        emitter.RegisterSession("channel", Session("channel-scheduling"));

        emitter.HasActiveSessions.ShouldBeTrue();
    }

    [Fact]
    public async Task EmitAsync_OnlyToolClientSessions_ReturnsFalse()
    {
        var emitter = Emitter();
        emitter.RegisterSession("tool", Session("Jack"));

        var delivered = await emitter.EmitAsync(Payload());

        delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task EmitAsync_ChannelClientSession_DeliversAndReturnsTrue()
    {
        var emitter = Emitter();
        emitter.RegisterSession("channel", Session("channel-scheduling"));

        var delivered = await emitter.EmitAsync(Payload());

        delivered.ShouldBeTrue();
    }

    private static ScheduleNotificationEmitter Emitter() =>
        new(NullLogger<ScheduleNotificationEmitter>.Instance);

    private static McpServer Session(string clientName)
    {
        var server = new Mock<McpServer>();
        server.SetupGet(s => s.ClientInfo).Returns(new Implementation { Name = clientName, Version = "1.0.0" });
        return server.Object;
    }

    private static ChannelMessageNotification Payload() => new()
    {
        ConversationId = "sched-1",
        Sender = "scheduler",
        Content = "run it",
        Timestamp = DateTimeOffset.UtcNow
    };
}