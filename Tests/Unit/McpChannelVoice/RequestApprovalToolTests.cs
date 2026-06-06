using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
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

public class RequestApprovalToolTests : IDisposable
{
    private readonly SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly Mock<ISpeechToText> _stt = new();
    private readonly CancellationTokenSource _pump = new();
    private readonly Task _pumpTask;
    private readonly VoiceConversationManager _manager;
    private readonly string _conversationId;
    private readonly IServiceProvider _services;

    public RequestApprovalToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        _pumpTask = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), _pump.Token);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        _manager = new VoiceConversationManager(
            factory.Object, _accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _conversationId = _manager.GetOrCreateAsync(_session, "agent-1", "hi", default).GetAwaiter().GetResult();

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(new VoiceSettings
            {
                FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 }
            })
            .AddSingleton<ISpeechToText>(_stt.Object)
            .AddSingleton(new WyomingClientSettings
            {
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 3000,
                MinSpeechMs = 100
            })
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton<ILogger<RequestApprovalTool>>(NullLogger<RequestApprovalTool>.Instance)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _pump.Cancel();
        _session.CompletePlayback();
        try
        { _pumpTask.GetAwaiter().GetResult(); }
        catch { /* OCE on teardown */ }
        _pump.Dispose();
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static AudioChunk Loud()
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

    // Whenever the tool opens a capture, feed one speech-then-silence answer into it.
    private Task FeedAnswersAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.HasActiveCapture)
            {
                _session.RouteAudio(Loud());
                _session.RouteAudio(Loud());
                _session.RouteAudio(Silent());
                _session.RouteAudio(Silent());
                _session.RouteAudio(Silent());
                await Task.Delay(60, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);

    private static ToolApprovalRequest MakeRequest(string toolName = "mcp__lib__download") =>
        new(null, toolName, new Dictionary<string, object?>());

    [Fact]
    public async Task NotifyMode_DoesNotSpeakOrWaitForResponse()
    {
        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // Auto-approved tool calls must not be narrated over voice — with no pending
        // reply text there is nothing to speak (the tool name itself is never read out).
        _tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyMode_WithPendingReplyText_SpeaksAcknowledgement()
    {
        // The agent wrote an acknowledgement before calling the (auto-approved) tool.
        _accumulator.Append(_conversationId, "Dame un momento");

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // The pending acknowledgement is spoken now so the user hears it while the tool runs.
        _tts.Verify(
            t => t.SynthesizeAsync("Dame un momento", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsDeclined()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "no thanks", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "hmm", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsDeclined()
    {
        var result = await RequestApprovalTool.McpRun(
            "ghost-01:999", ApprovalMode.Request, [MakeRequest()], _services);

        result.ShouldBe("declined");
    }

}