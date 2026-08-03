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

        _services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            new WyomingClientSettings
            {
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 3000,
                MinSpeechMs = 100
            });
    }

    private IServiceProvider BuildServices(
        VoiceSettings voice, WyomingClientSettings wyoming, Action<SilenceGateFactory>? seedRoomNoise = null)
    {
        var gates = new SilenceGateFactory(voice, wyoming, TimeProvider.System);
        seedRoomNoise?.Invoke(gates);
        return new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(voice)
            .AddSingleton<ISpeechToText>(_stt.Object)
            .AddSingleton(wyoming)
            .AddSingleton(gates)
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton<ILogger<RequestApprovalTool>>(NullLogger<RequestApprovalTool>.Instance)
            .AddSingleton(TimeProvider.System)
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
    // Five silent chunks (500 ms) — not three — because the capture opens with no
    // leading gap: the floor tracker's smoothed floor needs a full smoothing window
    // of true silence to descend enough for the next "Loud" burst to cross the entry
    // bar (a shorter gap is exactly what the smoothing is designed to ride through).
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

    // An answer given in an audible room: the capture opens on the background itself (the prompt has
    // just finished playing), the user speaks over it, then stops.
    private Task FeedAnswerOverBackgroundAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.HasActiveCapture)
            {
                foreach (var _ in Enumerable.Range(0, 5))
                { _session.RouteAudio(Level(4000)); }   // the room, which the capture has to open on top of
                _session.RouteAudio(Level(12_000));     // "sí, la de las tres"
                _session.RouteAudio(Level(12_000));
                _session.RouteAudio(Level(0));
                _session.RouteAudio(Level(0));
                await Task.Delay(60, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);

    // Constant-amplitude S16LE: for a flat signal the RMS is the amplitude itself.
    private static AudioChunk Level(short amplitude)
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)(amplitude >> 8);
        }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    // DemoteMarginDb above EnterMarginDb is what makes the capture-level accept bar bite: an answer
    // whose peak does not stand clear of the floor is thrown away rather than transcribed.
    private static WyomingClientSettings AudibleRoomSettings() => new()
    {
        SilenceRmsThreshold = 500,
        TrailingSilenceMs = 200,
        MaxUtteranceMs = 3000,
        MinSpeechMs = 100,
        DemoteMarginDb = 20
    };

    [Fact]
    public async Task RequestMode_NoRoomSample_DiscardsTheAnswerAgainstAnInflatedFloor()
    {
        // The capture measures its background from its own first frames, which here are the room the
        // user is already talking over. The floor freezes 10 dB under their voice, the answer fails
        // the accept bar, and a "sí" the satellite plainly heard is discarded.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            AudibleRoomSettings());

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswerOverBackgroundAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        await feed.CancelAsync();
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_WithARecordedRoomSample_HearsTheAnswer()
    {
        // Same room, same answer — but this satellite has produced a room reading recently, exactly
        // as the wake turn the user is answering did. Capping the floor with it keeps the answer.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });
        var services = BuildServices(
            new VoiceSettings { FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 } },
            AudibleRoomSettings(),
            gates => gates.RecordRoomLevel("kitchen-01", 200));

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswerOverBackgroundAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], services);

        await feed.CancelAsync();
        result.ShouldBe("approved");
    }

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
    public async Task RequestMode_AmbiguousThenNegative_ReturnsRejected()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "no thanks", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("rejected");
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
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_OpenAnswerCapture_IsVisibleAsArbitrationHolder()
    {
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        while (!_session.HasActiveCapture)
        { await Task.Delay(10); }

        // The approval mic is an open capture like any wake turn's: Rule B must be able to ask
        // it, retrospectively, what it heard during another satellite's wake-word span —
        // otherwise a leaked "ok nabu" during an approval wakes the other room unarbitrated.
        _session.GetCaptureActivity().ShouldNotBeNull();

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);
        (await run).ShouldBe("approved");
        await feed.CancelAsync();
    }

    [Fact]
    public async Task RequestMode_AnswerCaptureAbortedByArbiter_RejectsWithoutSttOrReprompt()
    {
        // If the partial audio were transcribed anyway, this setup would wrongly approve.
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        while (!_session.HasActiveCapture)
        { await Task.Delay(10); }

        // The arbiter stole the turn mid-answer (and already re-armed this satellite via
        // pause-satellite): the partial audio is not an answer, and there is no one left
        // here to re-prompt.
        _session.TryAbortCapture().ShouldBeTrue();

        (await run).ShouldBe("rejected");
        _stt.Verify(
            s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsRejected()
    {
        var result = await RequestApprovalTool.McpRun(
            "ghost-01:999", ApprovalMode.Request, [MakeRequest()], _services);

        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task RequestMode_UsesPerSatelliteGateOverride_NotGlobalSettings()
    {
        // Global RMS threshold is 500; this satellite's Gate override raises it far above
        // the "Loud" answer level. The capture must be built from session.Config (like
        // WyomingSatelliteHost does) rather than global wyoming.* settings alone — otherwise
        // this satellite's answer is wrongly heard as speech and gets approved.
        var session = new SatelliteSession("loud-room-01",
            new SatelliteConfig
            {
                Identity = "household",
                Room = "Loud Room",
                Gate = new GateSettings { SilenceRmsThreshold = 50_000 }
            });
        _sessions.Register(session);

        using var pump = new CancellationTokenSource();
        var pumpTask = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), pump.Token);
        try
        {
            var conversationId = await _manager.GetOrCreateAsync(session, "agent-1", "hi", default);

            _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

            using var feed = new CancellationTokenSource();
            var feeder = Task.Run(async () =>
            {
                while (!feed.IsCancellationRequested)
                {
                    if (session.HasActiveCapture)
                    {
                        session.RouteAudio(Loud());
                        session.RouteAudio(Loud());
                        session.RouteAudio(Silent());
                        session.RouteAudio(Silent());
                        session.RouteAudio(Silent());
                        await Task.Delay(60, feed.Token);
                    }
                    else
                    {
                        await Task.Delay(10, feed.Token);
                    }
                }
            }, feed.Token);

            var result = await RequestApprovalTool.McpRun(
                conversationId, ApprovalMode.Request, [MakeRequest()], _services);

            await feed.CancelAsync();

            // Under the fix the raised per-satellite threshold means "Loud" never
            // classifies as speech, so the capture times out with no speech and the
            // approval is declined instead of being transcribed and approved.
            result.ShouldBe("rejected");
        }
        finally
        {
            pump.Cancel();
            session.CompletePlayback();
            try
            { await pumpTask; }
            catch { /* OCE on teardown */ }
        }
    }
}