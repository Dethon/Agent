using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
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

public class SendReplyToolTests
{
    private readonly SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly VoiceConversationManager _manager;
    private readonly string _conversationId;
    private readonly IServiceProvider _services;
    private readonly List<VoiceEvent> _published = [];

    public SendReplyToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

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

        _conversationId = _manager.GetOrCreateAsync(_session, "agent-1", "hello", default).GetAwaiter().GetResult();

        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        var delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            _accumulator,
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<MetricEvent, CancellationToken>((e, _) =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
            });

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(metrics.Object)
            .AddSingleton(new VoiceSettings())
            .AddSingleton(delivery)
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

    private static async IAsyncEnumerable<AudioChunk> ThrowingAudio()
    {
        await Task.Yield();
        throw new InvalidOperationException("Wyoming TTS error: piper crashed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task McpRun_ReplySynthesisThrows_ResolvesTurnSilentInsteadOfWedgingTheMic()
    {
        // Regression guard for the FIX #4 follow-up: a reply synthesis failure (e.g. a Wyoming TTS
        // 'error' event, which now throws) must resolve the per-turn handshake via the reply job's
        // OnFailed -> SignalTurnSilent, so FollowUpConversation ends + re-arms wake. Without it the
        // mic stays wedged until the ~120s ReplyTimeoutMs. The chime and approval jobs already do this.
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => ThrowingAudio());

        _session.ResetTurn();
        var turn = _session.WaitForTurnSpokenAsync();

        await SendReplyTool.McpRun(_conversationId, "hola", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        _session.CompletePlayback();

        var spoke = await turn.WaitAsync(TimeSpan.FromSeconds(2)); // resolves promptly, not after a timeout
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        spoke.ShouldBeFalse(); // no audio actually played -> end conversation + re-arm, not "spoken"
    }

    [Fact]
    public async Task McpRun_Text_NotComplete_AccumulatesNoSynthesis()
    {
        var result = await SendReplyTool.McpRun(_conversationId, "hola ", ReplyContentType.Text, false, "m-1", _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_Text_Complete_SynthesisesAccumulatedText()
    {
        await SendReplyTool.McpRun(_conversationId, "hola ", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "mundo", ReplyContentType.Text, true, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_StreamComplete_SynthesisesAccumulatedText()
    {
        // Real agent streaming (see ReplyDispatcher.MapResponseUpdate): Text chunks are
        // emitted with isComplete=false; completion arrives only as a StreamComplete
        // event with empty content and no messageId. The reply must still be spoken.
        await SendReplyTool.McpRun(_conversationId, "hola ", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "mundo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Error_SpeaksErrorPrefix()
    {
        await SendReplyTool.McpRun(_conversationId, "boom", ReplyContentType.Error, true, "m-1", _services);
        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("boom")), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_PartialTextThenError_SpeaksPartialAndErrorOnceInOrder()
    {
        // Faulted agent run as ChatMonitor emits it: buffered Text (never isComplete) -> Error
        // (isComplete=false) -> trailing StreamComplete. The partial answer and the error must be
        // spoken together, in order, as a SINGLE utterance — never the error first with the leftover
        // partial spoken after it (the divergence from the Telegram/ServiceBus flush-on-error contract).
        await SendReplyTool.McpRun(_conversationId, "El tiempo es", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "boom", ReplyContentType.Error, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("El tiempo es") && s.Contains("boom")
                && s.IndexOf("El tiempo es", StringComparison.Ordinal) < s.IndexOf("boom", StringComparison.Ordinal)),
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Reasoning_DoesNothing()
    {
        var result = await SendReplyTool.McpRun(_conversationId, "thinking", ReplyContentType.Reasoning, false, null, _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsOk()
    {
        var result = await SendReplyTool.McpRun("ghost-01:999", "hi", ReplyContentType.Text, true, "m-1", _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_ToolCall_SpeaksBufferedPreambleWithoutResolvingTheTurn()
    {
        // nabu is told to emit a one-word acknowledgement ("Buscando.") before slow multi-tool work
        // so the user hears that something started. Text chunks are buffered and StreamComplete used
        // to be the only flush, so the ack was spoken glued to the front of the final answer —
        // arriving after the wait it existed to cover, and costing words for nothing. The first tool
        // call of a turn must flush and speak it. It must NOT resolve the turn handshake: that ends
        // FollowUpConversation and re-arms the mic mid-turn, before the answer is even spoken.
        _session.ResetTurn();
        var turn = _session.WaitForTurnSpokenAsync();

        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync("Buscando.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        var pump = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        _session.CompletePlayback();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        turn.IsCompleted.ShouldBeFalse(); // the preamble is not the end of the turn
    }

    [Fact]
    public async Task McpRun_PreambleThenAnswer_SpeaksThemAsSeparateUtterances()
    {
        // ReplyTextAccumulator concatenates with no separator, so before the tool-call flush the
        // satellite spoke a single "Buscando.Veintiún grados." utterance at the end of the turn.
        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "Veintiún grados.", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(t => t.SynthesizeAsync("Buscando.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _tts.Verify(t => t.SynthesizeAsync("Veintiún grados.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_ToolCall_NothingBuffered_SpeaksNothing()
    {
        // The overwhelmingly common case: the model went straight to a tool without a preamble.
        var result = await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_SecondToolCall_KeepsMidRunNarrationBufferedForTheAnswer()
    {
        // Only the FIRST tool call of a turn flushes. Anything the model says between later tool
        // rounds stays buffered and is spoken with the answer, so mid-run chatter can never become a
        // second utterance racing the reply into the playback queue.
        _session.ResetTurn();

        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "Ahora miro el termostato.", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-2", _services);

        _tts.Verify(t => t.SynthesizeAsync("Ahora miro el termostato.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_StreamComplete_PublishesTtsLatencyFromPlaybackNotEnqueue()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _session.MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromMilliseconds(1000)); // STT + agent thinking before the reply arrives

        await SendReplyTool.McpRun(_conversationId, "hola mundo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        // The reply job is enqueued (a non-blocking channel write) but playback hasn't run yet, so no
        // TTS-latency metric exists. The bug published TtsLatencyMs here, right after the enqueue (~0 ms).
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.TtsLatencyMs);

        // Drain the playback loop: the first synthesized chunk triggers OnFirstAudio, which publishes
        // the latency metrics from where synthesis actually happens.
        var pump = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        _session.CompletePlayback();
        await Task.Delay(80);
        time.Advance(TimeSpan.FromSeconds(1));
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        _published.Count(e => e.Metric == VoiceMetric.TtsLatencyMs).ShouldBe(1);
        var wake = _published.SingleOrDefault(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
        wake.ShouldNotBeNull();
        wake.DurationMs.ShouldBe(1000); // turn-start -> first audio (synthesis was instant in fake time)
    }
}