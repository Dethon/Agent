using System.Reflection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using SignalRChannel = McpChannelSignalR.Services;
using TelegramChannel = McpChannelTelegram.Services;
using ServiceBusChannel = McpChannelServiceBus.Services;

namespace Tests.Unit.Shared;

public class ChannelNotificationEmitterTests
{
    public static TheoryData<string, Func<IChannelNotificationEmitterAdapter>> Implementations => new()
    {
        {
            "SignalR",
            () =>
            {
                var sut = new SignalRChannel.ChannelNotificationEmitter(
                    new Mock<ILogger<SignalRChannel.ChannelNotificationEmitter>>().Object);
                return new ReflectionAdapter(sut);
            }
        },
        {
            "Telegram",
            () =>
            {
                var sut = new TelegramChannel.ChannelNotificationEmitter(
                    new Mock<ILogger<TelegramChannel.ChannelNotificationEmitter>>().Object);
                return new ReflectionAdapter(sut);
            }
        },
        {
            "ServiceBus",
            () =>
            {
                var sut = new ServiceBusChannel.ChannelNotificationEmitter(
                    new Mock<ILogger<ServiceBusChannel.ChannelNotificationEmitter>>().Object);
                return new ReflectionAdapter(sut);
            }
        },
    };

    [Theory]
    [MemberData(nameof(Implementations))]
    public void HasActiveSessions_Initially_ReturnsFalse(string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();

        sut.HasActiveSessions.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void RegisterSession_SetsHasActiveSessionsTrue(string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();

        sut.RegisterSession("sess-1", null!);

        sut.HasActiveSessions.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void UnregisterSession_RemovesSession(string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();
        sut.RegisterSession("sess-1", null!);

        sut.UnregisterSession("sess-1");

        sut.HasActiveSessions.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void UnregisterSession_UnknownId_DoesNotThrow(string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();

        Should.NotThrow(() => sut.UnregisterSession("nonexistent"));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task EmitMessageNotificationAsync_NoSessions_CompletesWithoutError(
        string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();

        await Should.NotThrowAsync(() =>
            sut.EmitMessageNotificationAsync("conv-1", "user", "hi", "agent1"));
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void RegisterSession_MultipleSessions_AllTracked(
        string _, Func<IChannelNotificationEmitterAdapter> factory)
    {
        var sut = factory();

        sut.RegisterSession("sess-1", null!);
        sut.RegisterSession("sess-2", null!);

        sut.HasActiveSessions.ShouldBeTrue();

        sut.UnregisterSession("sess-1");
        sut.HasActiveSessions.ShouldBeTrue();

        sut.UnregisterSession("sess-2");
        sut.HasActiveSessions.ShouldBeFalse();
    }
}

public interface IChannelNotificationEmitterAdapter
{
    bool HasActiveSessions { get; }
    void RegisterSession(string sessionId, McpServer? server);
    void UnregisterSession(string sessionId);
    Task EmitMessageNotificationAsync(string conversationId, string sender, string content, string agentId);
}

file class ReflectionAdapter(object inner) : IChannelNotificationEmitterAdapter
{
    private readonly Type _type = inner.GetType();

    public bool HasActiveSessions =>
        (bool)_type.GetProperty(nameof(HasActiveSessions))!.GetValue(inner)!;

    public void RegisterSession(string sessionId, McpServer? server) =>
        _type.GetMethod(nameof(RegisterSession))!.Invoke(inner, [sessionId, server]);

    public void UnregisterSession(string sessionId) =>
        _type.GetMethod(nameof(UnregisterSession))!.Invoke(inner, [sessionId]);

    public Task EmitMessageNotificationAsync(
        string conversationId, string sender, string content, string agentId) =>
        (Task)_type.GetMethod(nameof(EmitMessageNotificationAsync))!
            .Invoke(inner, [conversationId, sender, content, agentId, CancellationToken.None])!;
}
