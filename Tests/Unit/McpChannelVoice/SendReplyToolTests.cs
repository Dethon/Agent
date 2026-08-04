using Domain.Contracts;
using Domain.DTOs;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// All the tool does is pick a branch: a live session, a scheduled delivery target, or neither. The
// reply policy itself lives in ReplySpeaker and is tested there, without a container.
public class SendReplyToolTests
{
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        var accumulator = new ReplyTextAccumulator();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new VoiceSettings();
        var metrics = Mock.Of<IMetricsPublisher>();
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>());

        _services = new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton(new VoiceConversationManager(
                Mock.Of<IConversationFactory>(), accumulator, clock,
                TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance))
            .AddSingleton(new VoiceDeliveryRegistry(
                clock, TimeSpan.FromMinutes(5), accumulator,
                NullLogger<VoiceDeliveryRegistry>.Instance))
            .AddSingleton(new ReplySpeaker(
                accumulator, _tts.Object, settings, metrics, clock,
                NullLogger<ReplySpeaker>.Instance))
            .AddSingleton(new AnnouncementService(
                registry, sessions, _tts.Object, settings, metrics,
                NullLogger<AnnouncementService>.Instance))
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsOk()
    {
        var result = await SendReplyTool.McpRun(
            "ghost-01:999", "hi", ReplyContentType.Text, true, "m-1", _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_ConversationBoundToNoSatelliteAndNoDeliveryTarget_ReturnsOkWithoutSpeaking()
    {
        var result = await SendReplyTool.McpRun(
            "never-seen", "hi", ReplyContentType.Text, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }
}