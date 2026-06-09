using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class CreateConversationToolTests
{
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly Mock<IConversationFactory> _factory;
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["office-01"] = new() { Identity = "household", Room = "Office" },
            ["office-02"] = new() { Identity = "household", Room = "Office" }
        });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            new ReplyTextAccumulator(),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        _factory = new Mock<IConversationFactory>();
        _factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-sched");
                var topic = new TopicMetadata("topic-sched", identity.ChatId, identity.ThreadId, "mycroft",
                    "Scheduled task", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        _services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(_delivery)
            .AddSingleton<IConversationFactory>(_factory.Object)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_KnownSatellite_MintsConversationAndBindsSatelliteTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "AC reminder", "office-01");

        convId.ShouldNotBeNullOrWhiteSpace();
        var target = _delivery.Resolve(convId);
        target.ShouldNotBeNull();
        target!.SatelliteId.ShouldBe("office-01");
        target.All.ShouldBeNull();
    }

    [Fact]
    public async Task McpRun_AllAddress_BindsAllTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "broadcast", "all");

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public async Task McpRun_NullAddress_BindsAllTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "broadcast", null);

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public async Task McpRun_UnknownSatellite_Throws()
    {
        await Should.ThrowAsync<McpException>(() => CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "hi", "ghost-99"));
    }

    [Fact]
    public async Task McpRun_MultipleSatellites_BindsAllOfThem()
    {
        // A schedule delivering to several specific satellites (deliverTo coalesces them into a
        // comma-joined address) must speak on every one — bound as a single AnnounceTarget with
        // a satellite-id set, not just the last.
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "reminder", "office-01,office-02");

        var target = _delivery.Resolve(convId);
        target.ShouldNotBeNull();
        target!.SatelliteIds.ShouldBe(["office-01", "office-02"]);
    }

    [Fact]
    public async Task McpRun_MultipleSatellites_OneUnknown_Throws()
    {
        await Should.ThrowAsync<McpException>(() => CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "hi", "office-01,ghost-99"));
    }

    [Fact]
    public async Task McpRun_VoiceOnly_MintsVisibleTopicAndBindsSatellite()
    {
        // A voice-only scheduled delivery has no WebChat channel to attach to, so it must
        // mint a real topic via the shared factory — that persists TopicMetadata and makes
        // the schedule appear in WebChat under the agent, consistent with interactive voice
        // conversations (which the user sees today).
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "AC reminder", "office-01");

        convId.ShouldBe(ConversationIdGenerator.CreateFor("topic-sched").ConversationId);
        _factory.Verify(
            f => f.CreateAsync(
                It.Is<CreateConversationParams>(p => p.AgentId == "mycroft" && p.TopicName == "Scheduled task"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _delivery.Resolve(convId)!.SatelliteId.ShouldBe("office-01");
    }

    [Fact]
    public async Task McpRun_WithExistingConversationId_AttachesWithoutMintingTopic()
    {
        // When a scheduled delivery targets both a WebChat channel and voice, the WebChat
        // channel mints the conversation and the voice channel must ATTACH to that same id
        // for TTS routing — never mint its own topic. Minting one leaves an empty, duplicate
        // "Scheduled task" thread in WebChat (the shared ConversationFactory persists it).
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "AC reminder", "office-01", "shared-123");

        convId.ShouldBe("shared-123");
        _delivery.Resolve("shared-123")!.SatelliteId.ShouldBe("office-01");
        _factory.Verify(
            f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}