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

    [Fact]
    public async Task TransferBinding_ActiveConversation_MovesToTargetSatellite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var conversationId = await sut.GetOrCreateAsync(sessionA, "agent-1", "hola", default);

        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBeTrue();

        sut.GetActiveConversationId("sat-b").ShouldBe(conversationId);
        sut.GetActiveConversationId("sat-a").ShouldBeNull();
        sut.ResolveSatelliteId(conversationId).ShouldBe("sat-b");

        // A late-firing timer tied to sat-a's original creation (due at t=5min) must not resurrect
        // or clear anything now that the conversation lives under sat-b with its own fresh timer
        // (due at t=2min+5min=7min).
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationId);
    }

    [Fact]
    public void TransferBinding_NoActiveConversation_ReturnsFalse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);

        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBeFalse();
    }

    [Fact]
    public async Task TransferBinding_TargetHadItsOwnConversation_TargetEntryIsDisplaced()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var sessionB = new SatelliteSession("sat-b", new SatelliteConfig { Identity = "household", Room = "Office B" });
        var conversationA = await sut.GetOrCreateAsync(sessionA, "agent-1", "hola", default);
        var conversationB = await sut.GetOrCreateAsync(sessionB, "agent-1", "hey", default);

        clock.Advance(TimeSpan.FromMinutes(2));
        sut.TransferBinding("sat-a", "sat-b", clock.GetTimestamp()).ShouldBeTrue();

        sut.GetActiveConversationId("sat-b").ShouldBe(conversationA);
        sut.ResolveSatelliteId(conversationB).ShouldBeNull();

        // conversationB's original idle timer (due at t=5min from its own creation) must not
        // resurrect it or clear the slot out from under the transferred conversation, which now
        // has its own fresh timer due later (transfer happened at t=2min, so due at t=7min).
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationA);
        sut.ResolveSatelliteId(conversationB).ShouldBeNull();
    }

    [Fact]
    public async Task TransferBinding_TargetBoundAfterClaim_SkipsTheStaleHandoff()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);
        var sessionA = new SatelliteSession("sat-a", new SatelliteConfig { Identity = "household", Room = "Office A" });
        var sessionB = new SatelliteSession("sat-b", new SatelliteConfig { Identity = "household", Room = "Office B" });
        var conversationA = await sut.GetOrCreateAsync(sessionA, "agent-1", "hola", default);

        clock.Advance(TimeSpan.FromMinutes(1));
        var claimedAt = clock.GetTimestamp(); // sat-b's wake claim arrives
        clock.Advance(TimeSpan.FromSeconds(2)); // decision delayed (e.g. a wedged loser's re-arm)
        var conversationB = await sut.GetOrCreateAsync(sessionB, "agent-1", "enciende la luz", default);

        // sat-b's own turn already ran and bound its own conversation after the claim: the
        // handoff is stale, and displacing conversationB would silently drop its in-flight reply.
        sut.TransferBinding("sat-a", "sat-b", claimedAt).ShouldBeFalse();

        sut.ResolveSatelliteId(conversationB).ShouldBe("sat-b");
        sut.GetActiveConversationId("sat-b").ShouldBe(conversationB);
        sut.GetActiveConversationId("sat-a").ShouldBe(conversationA);
    }
}