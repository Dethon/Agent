using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The other half of the speaker: an answer written for a satellite that was not listening when it
// was written, settled through the announcement service instead of a live session.
public class ReplySpeakerScheduledDeliveryTests
{
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly SatelliteConfig _config = new() { Identity = "household", Room = "Office" };
    private readonly AnnouncementService _announcer;
    private readonly ReplySpeaker _speaker;

    public ReplySpeakerScheduledDeliveryTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig> { ["office-01"] = _config });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            _accumulator,
            NullLogger<VoiceDeliveryRegistry>.Instance);

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        var settings = new VoiceSettings();
        var metrics = Mock.Of<IMetricsPublisher>();
        _announcer = new AnnouncementService(
            registry, _sessions, _tts.Object, settings, metrics, NullLogger<AnnouncementService>.Instance);

        _speaker = new ReplySpeaker(
            _accumulator, _tts.Object, settings, metrics,
            new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<ReplySpeaker>.Instance);
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

    private Task Deliver(string content, ReplyContentType contentType, bool isComplete) =>
        _speaker.DeliverScheduledAsync(
            new SendReplyParams
            {
                ConversationId = "sched-conv",
                Content = content,
                ContentType = contentType,
                IsComplete = isComplete
            },
            _delivery.Resolve("sched-conv")!,
            _delivery,
            _announcer);

    [Fact]
    public async Task DeliverScheduledAsync_OnStreamComplete_AnnouncesAccumulatedTextToSatellite()
    {
        RegisterLiveSession();
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await Deliver("The AC ", ReplyContentType.Text, false);
        await Deliver("is on.", ReplyContentType.Text, false);
        await Deliver("", ReplyContentType.StreamComplete, true);

        _tts.Verify(t => t.SynthesizeAsync("The AC is on.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task DeliverScheduledAsync_Error_DoesNotSpeakAndUnbinds()
    {
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await Deliver("partial", ReplyContentType.Text, false);
        await Deliver("boom", ReplyContentType.Error, false);

        _tts.VerifyNoOtherCalls();
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task DeliverScheduledAsync_OfflineSatellite_DoesNotThrowOrSpeak()
    {
        // Configured satellite but no live session registered -> AnnouncementService records "offline".
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await Deliver("anyone home?", ReplyContentType.Text, false);
        await Deliver("", ReplyContentType.StreamComplete, true);

        _tts.VerifyNoOtherCalls();
    }
}