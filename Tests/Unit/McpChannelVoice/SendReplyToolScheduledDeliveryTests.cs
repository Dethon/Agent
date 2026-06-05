using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolScheduledDeliveryTests
{
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly SatelliteConfig _config = new() { Identity = "household", Room = "Office" };
    private readonly IServiceProvider _services;

    public SendReplyToolScheduledDeliveryTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig> { ["office-01"] = _config });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var factory = new Mock<IConversationFactory>();
        var manager = new VoiceConversationManager(
            factory.Object, _accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        var settings = new VoiceSettings();
        var metrics = Mock.Of<IMetricsPublisher>();
        var announcer = new AnnouncementService(
            registry, _sessions, _tts.Object, settings, metrics, NullLogger<AnnouncementService>.Instance);

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(registry)
            .AddSingleton(_delivery)
            .AddSingleton(_accumulator)
            .AddSingleton(manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(metrics)
            .AddSingleton(settings)
            .AddSingleton(announcer)
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAudio(string label)
    {
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(label),
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private void RegisterLiveSession() => _sessions.Register(new SatelliteSession("office-01", _config));

    [Fact]
    public async Task ScheduledDelivery_OnStreamComplete_AnnouncesAccumulatedTextToSatellite()
    {
        RegisterLiveSession();
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "The AC ", ReplyContentType.Text, false, null, _services);
        await SendReplyTool.McpRun("sched-conv", "is on.", ReplyContentType.Text, false, null, _services);
        await SendReplyTool.McpRun("sched-conv", "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(t => t.SynthesizeAsync("The AC is on.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task ScheduledDelivery_Error_DoesNotSpeakAndUnbinds()
    {
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "partial", ReplyContentType.Text, false, null, _services);
        var result = await SendReplyTool.McpRun("sched-conv", "boom", ReplyContentType.Error, false, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task ScheduledDelivery_OfflineSatellite_DoesNotThrowOrSpeak()
    {
        // Configured satellite but no live session registered -> AnnouncementService records "offline".
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "anyone home?", ReplyContentType.Text, false, null, _services);
        var result = await SendReplyTool.McpRun("sched-conv", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnboundConversation_ReturnsOkWithoutSpeaking()
    {
        var result = await SendReplyTool.McpRun("never-seen", "hi", ReplyContentType.Text, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }
}