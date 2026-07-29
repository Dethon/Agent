using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using ServiceBusChannel = McpChannelServiceBus.Services;
using VoiceChannel = McpChannelVoice.Services;

namespace Tests.Unit.Shared;

// Covers the session-push emitters that have not yet moved to the ChannelInbox long-poll model.
// A channel drops its row here once it migrates (its inbox behaviour is pinned by
// Tests/Integration/Channels/ChannelReceiveContractTests and its own emitter tests); the file
// goes away with the last migration.
public class ChannelNotificationEmitterTests
{
    public static TheoryData<string, Func<IChannelNotificationEmitterAdapter>> Implementations => new()
    {
        {
            "ServiceBus",
            () =>
            {
                var sut = new ServiceBusChannel.ChannelNotificationEmitter(
                    new Mock<ILogger<ServiceBusChannel.ChannelNotificationEmitter>>().Object);
                return new ReflectionAdapter(sut);
            }
        },
        {
            "Voice",
            () =>
            {
                var sut = new VoiceChannel.ChannelNotificationEmitter(
                    new Mock<ILogger<VoiceChannel.ChannelNotificationEmitter>>().Object);
                return new ReflectionAdapter(sut);
            }
        },
    };

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
        string conversationId, string sender, string content, string agentId)
    {
        // Bind by parameter name so channels that extend the contract (voice adds an optional
        // `location`) are still exercised uniformly by the shared lifecycle tests.
        var method = _type.GetMethod(nameof(EmitMessageNotificationAsync))!;
        var args = method.GetParameters().Select(p => (object?)(p.Name switch
        {
            "conversationId" => conversationId,
            "sender" => sender,
            "content" => content,
            "agentId" => agentId,
            _ => p.ParameterType == typeof(CancellationToken) ? CancellationToken.None : null
        })).ToArray();
        return (Task)method.Invoke(inner, args)!;
    }
}