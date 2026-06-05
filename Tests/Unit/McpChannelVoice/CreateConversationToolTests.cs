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
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["office-01"] = new() { Identity = "household", Room = "Office" }
        });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
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
            .AddSingleton<IConversationFactory>(factory.Object)
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
}