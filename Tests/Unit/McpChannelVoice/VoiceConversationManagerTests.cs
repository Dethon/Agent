using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceConversationManagerTests
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static (VoiceConversationManager Sut, Mock<IConversationFactory> Factory) Build(FakeTimeProvider clock)
    {
        var factory = new Mock<IConversationFactory>();
        var counter = 0;
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                counter++;
                var topicId = $"topic-{counter}";
                var identity = ConversationIdGenerator.CreateFor(topicId);
                var topic = new TopicMetadata(topicId, identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", clock.GetUtcNow(), null);
                return new ConversationCreation(identity, topic);
            });

        var sut = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), clock, _lifetime,
            NullLogger<VoiceConversationManager>.Instance);
        return (sut, factory);
    }

    [Fact]
    public async Task FirstUtterance_MintsConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        var id = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);

        id.ShouldNotBeNullOrWhiteSpace();
        sut.GetActiveConversationId("kitchen-01").ShouldBe(id);
        sut.ResolveSatelliteId(id).ShouldBe("kitchen-01");
        factory.Verify(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecondUtteranceWithinWindow_ReusesAndRenews()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = await sut.GetOrCreateAsync(Session(), "agent-1", "again", default);

        second.ShouldBe(first);
        factory.Verify(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()), Times.Once);

        clock.Advance(TimeSpan.FromMinutes(4));
        sut.GetActiveConversationId("kitchen-01").ShouldBe(first);
    }

    [Fact]
    public async Task AfterIdleExpiry_NextUtteranceMintsNewConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        sut.GetActiveConversationId("kitchen-01").ShouldBeNull();
        sut.ResolveSatelliteId(first).ShouldBeNull();

        var second = await sut.GetOrCreateAsync(Session(), "agent-1", "fresh", default);
        second.ShouldNotBe(first);
        sut.ResolveSatelliteId(second).ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task BuildsTopicNameFromIdentityAndRoom()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);

        factory.Verify(f => f.CreateAsync(
            It.Is<CreateConversationParams>(p =>
                p.AgentId == "agent-1" &&
                p.TopicName == "household @ Kitchen" &&
                p.Sender == "household" &&
                p.InitialPrompt == "hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}