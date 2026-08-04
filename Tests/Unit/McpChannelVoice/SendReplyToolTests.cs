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
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

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

        _services = BuildServices(new VoiceSettings());
    }

    private IServiceProvider BuildServices(VoiceSettings settings)
    {
        var delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            _accumulator,
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
            });

        return new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(metrics.Object)
            .AddSingleton(settings)
            .AddSingleton(delivery)
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .AddSingleton<TimeProvider>(_clock)
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
        // failed outcome -> the segment fails and the turn settles silent, so FollowUpConversation ends +
        // re-arms wake. Without it the
        // mic stays wedged until the ~120s ReplyTimeoutMs. The chime and approval jobs already do this.
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => ThrowingAudio());

        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        await SendReplyTool.McpRun(_conversationId, "hola", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        _session.Playback.Complete();

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
        _session.Turn.Reset();
        var turn = _session.Turn.AwaitSpoken();

        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync("Buscando.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        _session.Playback.Complete();
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
        _session.Turn.Reset();

        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "Ahora miro el termostato.", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-2", _services);

        _tts.Verify(t => t.SynthesizeAsync("Ahora miro el termostato.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_StreamComplete_PublishesTtsLatencyFromPlaybackNotEnqueue()
    {
        // One clock throughout: a second FakeTimeProvider here used to stamp EnqueuedAt while this
        // one drained playback, so the queue-wait span was computed across two unrelated timelines.
        _session.Playback.MarkTurnStart(_clock.GetTimestamp());
        _session.Turn.MarkDispatched(_clock.GetTimestamp()); // turn-anchored spans need the dispatch proof
        _clock.Advance(TimeSpan.FromMilliseconds(1000)); // STT + agent thinking before the reply arrives

        await SendReplyTool.McpRun(_conversationId, "hola mundo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        // The reply job is enqueued (a non-blocking channel write) but playback hasn't run yet, so no
        // TTS-latency metric exists. The bug published TtsLatencyMs here, right after the enqueue (~0 ms).
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.TtsLatencyMs);

        // Drain the playback loop: the first synthesized chunk triggers OnFirstAudio, which publishes
        // the latency metrics from where synthesis actually happens.
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, _clock);
        _session.Playback.Complete();
        await Task.Delay(80);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        _published.Count(e => e.Metric == VoiceMetric.TtsLatencyMs).ShouldBe(1);
        var wake = _published.SingleOrDefault(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
        wake.ShouldNotBeNull();
        wake.DurationMs.ShouldBe(1000); // turn-start -> first audio (synthesis was instant in fake time)
    }

    [Fact]
    public async Task McpRun_StreamComplete_PublishesSpeechEndAndQueueWaitMetrics()
    {
        _session.Turn.Reset();
        _session.Playback.MarkTurnStart(_clock.GetTimestamp());
        _session.Playback.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, _clock);
        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        var published = _published.Select(e => e.Metric).ToList();
        published.ShouldContain(VoiceMetric.SpeechEndToFirstAudioMs);
        published.ShouldContain(VoiceMetric.ReplyQueueWaitMs);
    }

    [Fact]
    public async Task McpRun_ReplyWithNoDispatchStamp_PublishesNoTurnAnchoredSpans()
    {
        // "Recuérdame en dos minutos" fires into a voice-minted conversation whose mapping is still
        // alive (ConversationLifetime is 5 minutes) and whose satellite session is live, so McpRun
        // routes it down the utterance path — create_conversation no-ops in that state. The turn
        // anchors from the earlier real turn are never invalidated, so without a gate this publishes
        // SpeechEndToFirstAudioMs ≈ 120000: one sample that wrecks Avg/P95/Max on the headline metric.
        _session.Turn.Reset();
        _session.Playback.MarkTurnStart(_clock.GetTimestamp());
        _session.Playback.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _clock.Advance(TimeSpan.FromMinutes(2));

        await SendReplyTool.McpRun(_conversationId, "son las diez", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, _clock);
        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs);
        _published.ShouldNotContain(e => e.Metric == VoiceMetric.WakeToFirstAudioMs);
        // TtsLatencyMs and ReplyQueueWaitMs are anchored on this job alone, so they stay honest.
        _published.ShouldContain(e => e.Metric == VoiceMetric.TtsLatencyMs);
        _published.ShouldContain(e => e.Metric == VoiceMetric.ReplyQueueWaitMs);
    }

    [Fact]
    public async Task McpRun_StreamCompleteAfterDispatch_PublishesAgentRoundTrip()
    {
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(4)); // the agent thinking

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var roundTrip = _published.SingleOrDefault(e => e.Metric == VoiceMetric.AgentRoundTripMs);
        roundTrip.ShouldNotBeNull();
        roundTrip!.DurationMs.ShouldBe(4000);
    }

    [Fact]
    public async Task McpRun_StreamCompleteWithoutDispatch_PublishesNoAgentRoundTrip()
    {
        // An announce/scheduled delivery never went through a transcript dispatch, so there is no
        // round trip to report — publishing one would invent a span.
        _session.Turn.Reset();

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.AgentRoundTripMs);
    }

    [Fact]
    public async Task McpRun_SecondReplyAfterDispatchAlreadyConsumed_PublishesNoAgentRoundTrip()
    {
        // A live session's conversation can receive a schedule-fired or agent-initiated reply
        // (CreateConversationTool routes it through this same session) with no fresh transcript
        // dispatch behind it. The stamp from the earlier real turn must not still be sitting there
        // for this second, unrelated reply to pick up and report as an invented round trip.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _session.Turn.Reset();
        await SendReplyTool.McpRun(_conversationId, "otra vez", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _published.Count(e => e.Metric == VoiceMetric.AgentRoundTripMs).ShouldBe(1);
    }

    [Fact]
    public async Task McpRun_PreambleBeforeDispatchedAnswer_DoesNotConsumeTheStampForTheAnswer()
    {
        // The preamble ("Buscando") is spoken with isReply:false. If it consumed the dispatch
        // stamp, the real answer that follows would lose its AgentRoundTripMs metric entirely.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2));

        await SendReplyTool.McpRun(_conversationId, "Buscando.", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.ToolCall, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "Veintiún grados.", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var roundTrip = _published.SingleOrDefault(e => e.Metric == VoiceMetric.AgentRoundTripMs);
        roundTrip.ShouldNotBeNull();
        roundTrip!.DurationMs.ShouldBe(2000);
    }

    [Fact]
    public async Task McpRun_TwoDispatchedTurnsInSequence_PublishesAgentRoundTripBothTimes()
    {
        // A follow-up turn re-arms the stamp: each dispatch stands on its own, independent of the
        // previous turn's already-consumed one.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2));
        await SendReplyTool.McpRun(_conversationId, "primero", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(3));
        await SendReplyTool.McpRun(_conversationId, "segundo", ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var roundTrips = _published.Where(e => e.Metric == VoiceMetric.AgentRoundTripMs).ToList();
        roundTrips.Count.ShouldBe(2);
        roundTrips[0].DurationMs.ShouldBe(2000);
        roundTrips[1].DurationMs.ShouldBe(3000);
    }

    [Fact]
    public async Task McpRun_AgentRoundTripPublishCost_LandsInTheQueueWaitRatherThanNoSpan()
    {
        // EnqueuedAt used to be stamped AFTER the AgentRoundTripMs publish, so whatever that publish
        // cost belonged to no span and the decomposition silently lost a slice on every turn.
        // EnqueuedAt is taken first and the round trip measured TO it, making the two spans exactly
        // adjacent. Modelled by a publisher that costs fake time.
        const int publishMs = 50;
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
                _clock.Advance(TimeSpan.FromMilliseconds(publishMs));
            });

        var services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(metrics.Object)
            .AddSingleton(new VoiceSettings())
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .AddSingleton<TimeProvider>(_clock)
            .BuildServiceProvider();

        // All three anchors coincide, so SpeechEndToFirstAudioMs is the whole dispatch -> first-audio
        // span and the three sub-spans must tile it exactly.
        _session.Turn.Reset();
        _session.Playback.MarkTurnStart(_clock.GetTimestamp());
        _session.Playback.MarkSpeechEnd(_clock.GetTimestamp(), endpointTailMs: 0, _clock);
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(2)); // the agent thinking

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, services);

        _clock.Advance(TimeSpan.FromMilliseconds(400)); // the reply waits behind another job

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, _clock);
        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        var roundTrip = _published.Single(e => e.Metric == VoiceMetric.AgentRoundTripMs).DurationMs;
        var queueWait = _published.Single(e => e.Metric == VoiceMetric.ReplyQueueWaitMs).DurationMs;
        var tts = _published.Single(e => e.Metric == VoiceMetric.TtsLatencyMs).DurationMs;
        var whole = _published.Single(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs).DurationMs;

        roundTrip.ShouldBe(2000);
        queueWait.ShouldBe(400 + publishMs); // the publish's own cost is inside the queue wait
        (roundTrip + queueWait + tts).ShouldBe(whole);
    }

    [Fact]
    public async Task McpRun_TextFormingACompleteSentence_SpeaksItBeforeStreamComplete()
    {
        // The whole point of streaming: the answer's opening is already synthesizing while the
        // agent is still generating the rest, instead of waiting for the turn to end.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. Por la noche ",
            ReplyContentType.Text, false, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync(
            "Mañana por la tarde hará sol y unos veintidós grados.",
            It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_PartialSentence_SpeaksNothingYet()
    {
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId, "Mañana por la tarde hará sol y unos",
            ReplyContentType.Text, false, "m-1", _services);

        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_StreamedAnswer_SettlesTheTurnOnceAfterEverySegmentDrains()
    {
        // The regression that streaming introduces if the handshake is left per-job: sentence one
        // draining would end FollowUpConversation, chiming and reopening the mic over the rest.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();

        var written = new List<string>();
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = _session.Playback.RunAsync((chunk, _) =>
        {
            lock (written)
            { written.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span)); }
            wrote.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Segment one, played to completion with the agent still generating. This is the moment the
        // per-job handshake got wrong: the turn must NOT settle here.
        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. ",
            ReplyContentType.Text, false, "m-1", _services);
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // let the loop finish the drain wait and settle the segment

        written.Count.ShouldBe(1);        // exactly the opening sentence has reached the satellite
        turn.IsCompleted.ShouldBeFalse();

        await SendReplyTool.McpRun(_conversationId,
            "Por la noche bajará bastante y habrá algo de viento del norte. ",
            ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        // Awaited rather than read: the segment settles from the job's outcome, and the queue
        // signals an outcome without waiting for whoever reacts to it.
        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        written.Count.ShouldBeGreaterThan(1); // it really did stream in pieces
    }

    [Fact]
    public async Task McpRun_StreamedAnswer_PublishesTurnAnchoredSpansOnlyOnce()
    {
        // SpeechEndToFirstAudioMs/WakeToFirstAudioMs measure time-to-FIRST-audio, so N segments must
        // not publish N samples. The single-use dispatch stamp is what enforces it.
        _session.Turn.Reset();
        _session.Playback.MarkTurnStart(_clock.GetTimestamp());
        _session.Playback.MarkSpeechEnd(_clock.GetTimestamp(), 0, _clock);
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. ",
            ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId,
            "Por la noche bajará bastante y habrá algo de viento del norte. ",
            ReplyContentType.Text, false, "m-2", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        _published.Count(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs).ShouldBe(1);
        _published.Count(e => e.Metric == VoiceMetric.WakeToFirstAudioMs).ShouldBe(1);
        _published.Count(e => e.Metric == VoiceMetric.AgentRoundTripMs).ShouldBe(1);
    }

    [Fact]
    public async Task McpRun_StreamingDisabled_BuffersUntilStreamComplete()
    {
        // The kill switch has to genuinely restore the old behaviour.
        var services = BuildServices(new VoiceSettings
        {
            Tts = new TtsSettings { Streaming = new StreamingTtsConfig { Enabled = false } }
        });
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. ",
            ReplyContentType.Text, false, "m-1", services);

        _tts.VerifyNoOtherCalls();

        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, services);

        _tts.Verify(t => t.SynthesizeAsync(
            It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }


    [Fact]
    public async Task McpRun_QueuedSegments_StartSynthesisWithoutWaitingForThePlaybackLoop()
    {
        // Prefetch: the TTS request must go out when the segment is queued. The playback loop is
        // sequential and will not touch a job's audio until the previous one has finished its
        // real-time drain, so leaving it lazy puts a full round trip into every sentence seam.
        // No playback loop runs in this test at all — every recorded synthesis is therefore one the
        // loop did not ask for.
        var synthesized = new List<string>();
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => Recording(text, synthesized));

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. ",
            ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId,
            "Por la noche bajará bastante la temperatura y habrá algo de viento del norte, así que conviene cerrar las ventanas antes de irse a dormir esta noche. ",
            ReplyContentType.Text, false, "m-2", _services);

        await WaitForCountAsync(synthesized, 2, TimeSpan.FromSeconds(5));
        lock (synthesized)
        { synthesized.Count.ShouldBe(2); }
    }

    [Fact]
    public async Task McpRun_PrefetchDisabled_LeavesSynthesisToThePlaybackLoop()
    {
        var synthesized = new List<string>();
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => Recording(text, synthesized));

        // Prefetching is switched off by the queue having no prefetch size, which is what the
        // connection builds from Tts.Streaming.Prefetch.
        _sessions.Register(new SatelliteSession(
            "kitchen-01", _session.Config, new PlaybackQueue(prefetchBufferChunks: null)));
        var session = _sessions.Get("kitchen-01")!;
        session.Turn.Reset();
        session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId,
            "Mañana por la tarde hará sol y unos veintidós grados. ",
            ReplyContentType.Text, false, "m-1", _services);

        await Task.Delay(200);
        lock (synthesized)
        { synthesized.ShouldBeEmpty(); }
    }

    [Fact]
    public async Task McpRun_ReplySegmentPreemptedMidPlayback_SettlesTheTurnThroughTheRealPlaybackLoop()
    {
        // Drives a real reply job through RunPlaybackLoopAsync and preempts it, which is the only
        // way to reach the preempted outcome -> segment.Fail() release. Without it the turn waits
        // out the ~120s ReplyTimeoutMs with the mic wedged.
        // Signalled from the WRITER, not the audio source: the prefetch pump starts synthesising at
        // enqueue, so a source-side signal fires before the playback loop owns the job and the
        // preempt would land on nothing.
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => Gated());

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();
        var pump = _session.Playback.RunAsync(
            async (_, _) => { playing.TrySetResult(); await Task.Yield(); },
            CancellationToken.None, _clock);

        await SendReplyTool.McpRun(_conversationId, "Hola mundo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the segment is mid-drain
        _session.Playback.PreemptCurrent();
        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        // Settles promptly instead of wedging. Silent, not Spoken: no segment ever drained, so the
        // conversation ends and wake re-arms rather than opening a follow-up window over the alarm
        // that cut in.
        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();
    }

    [Fact]
    public async Task McpRun_PlaybackQueueRefusesTheSegment_SettlesTheTurnAndNeverStartsTheSynthesis()
    {
        // A satellite that disconnects mid-answer completes the playback queue, so every further
        // enqueue is refused. The segment must still be released — and the synthesis must never
        // start at all, because the queue only takes ownership of audio for a job it accepted.
        var pulled = 0;
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>(
                (_, _, _) => Counting(() => Interlocked.Increment(ref pulled)));

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var turn = _session.Turn.AwaitSpoken();
        _session.Playback.Complete();    // the queue is closed: every enqueue now refuses

        await SendReplyTool.McpRun(_conversationId, "Hola mundo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        (await turn.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();  // nothing ever played
        Volatile.Read(ref pulled).ShouldBe(0);
    }

    private static async IAsyncEnumerable<AudioChunk> Counting(Action onPull)
    {
        onPull();
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    [Fact]
    public async Task McpRun_QueuedSegmentPreemptedBeforeItStarts_ReleasesItsPrefetchedSynthesis()
    {
        // An alarm marks every queued job for preemption, and the loop cancels a marked job BEFORE
        // touching its audio — so the release that normally runs when the consumer's enumerator is
        // torn down never happens. Without an explicit dispose on the preempt path the prefetch pump
        // stays parked on a full buffer holding an open TTS response: one leaked synthesis per
        // queued sentence, on every alarm that cuts into a streamed reply.
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synthesisCalls = 0;
        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) =>
                Interlocked.Increment(ref synthesisCalls) == 1 ? Gated() : DisposeTracking(disposed));

        var services = BuildServices(new VoiceSettings
        {
            Tts = new TtsSettings
            { Streaming = new StreamingTtsConfig { FirstSegmentMinChars = 10, MinChars = 10 } }
        });

        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        var pump = _session.Playback.RunAsync(
            async (_, _) => { playing.TrySetResult(); await Task.Yield(); },
            CancellationToken.None, _clock);

        await SendReplyTool.McpRun(_conversationId, "Primera frase completa. ",
            ReplyContentType.Text, false, "m-1", services);
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));   // segment one is mid-drain
        await SendReplyTool.McpRun(_conversationId, "Segunda frase completa. ",
            ReplyContentType.Text, false, "m-1", services);       // segment two sits queued

        _session.Playback.Enqueue(new PlaybackJob(
            Label: "alarm", Kind: PlaybackKind.Alarm, Priority: AnnouncePriority.High,
            Audio: NoAudio()));

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _session.Playback.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task McpRun_StreamingWithNoRoomInThePlaybackQueue_LeavesTheTextBufferedInsteadOfDroppingIt()
    {
        // TryTakeSpeakable removes a sentence run from the accumulator BEFORE the enqueue is
        // accepted, so a queue with no room silently swallowed whole sentences: the user heard an
        // answer with a hole in it and the turn still settled Spoken. Text that cannot be queued
        // must stay buffered for the next flush.
        var services = BuildServices(new VoiceSettings
        {
            Tts = new TtsSettings
            {
                Streaming = new StreamingTtsConfig { FirstSegmentMinChars = 10, MinChars = 10 }
            }
        });

        // The depth limit is the queue's now, so a satellite with room for one segment is a queue
        // built with room for one segment.
        _sessions.Register(new SatelliteSession(
            "kitchen-01", _session.Config, new PlaybackQueue(replyMaxDepth: 1, announceMaxDepth: 1)));
        var session = _sessions.Get("kitchen-01")!;
        session.Turn.Reset();
        session.Turn.MarkDispatched(_clock.GetTimestamp());
        // No playback loop is running, so nothing drains and the queue stays at its cap.

        await SendReplyTool.McpRun(_conversationId, "Primera frase completa. ",
            ReplyContentType.Text, false, "m-1", services);
        await SendReplyTool.McpRun(_conversationId, "Segunda frase completa. ",
            ReplyContentType.Text, false, "m-1", services);
        await SendReplyTool.McpRun(_conversationId, "Tercera frase completa. ",
            ReplyContentType.Text, false, "m-1", services);

        // Whatever could not be queued is still there for StreamComplete to flush.
        var leftover = _accumulator.Flush(_conversationId);
        leftover.ShouldContain("Segunda frase completa.");
        leftover.ShouldContain("Tercera frase completa.");
    }

    [Fact]
    public async Task McpRun_TurnEndsWithNoAudio_DoesNotLeaveTheDispatchStampForALaterReply()
    {
        // A tool-only turn never reaches SpeakAsync, so TryConsumeDispatchedAt never runs and the
        // stamp outlives the turn. A schedule firing into the same live session then consumes it and
        // publishes AgentRoundTripMs/WakeToFirstAudioMs anchored to the old turn — minutes of
        // staleness on the headline metrics, which is what the stamp gate exists to prevent.
        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _session.Turn.TryConsumeDispatchedAt().ShouldBeNull();
    }

    // One chunk, then parks: the job stays mid-drain until something cancels it, which is what makes
    // the preempt path reachable at all.
    private static async IAsyncEnumerable<AudioChunk> Gated(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Delay(Timeout.Infinite, token);
    }

    // Parks on the prefetch's own token so DisposeAsync can actually unwind it, which is the
    // behaviour under test: disposal is what releases an in-flight synthesis nobody will enumerate.
    private static async IAsyncEnumerable<AudioChunk> DisposeTracking(TaskCompletionSource disposed,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        try
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Delay(Timeout.Infinite, token);
        }
        finally
        {
            disposed.TrySetResult();
        }
    }

    // Zero chunks: the alarm must drain instantly under the frozen test clock, so it never reaches
    // the real-time drain wait that only a clock advance would release.
    private static async IAsyncEnumerable<AudioChunk> NoAudio()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<AudioChunk> Recording(string text, List<string> sink)
    {
        lock (sink)
        { sink.Add(text); }
        await Task.Yield();
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(text),
            Format = AudioFormat.WyomingStandard
        };
    }

    private static async Task WaitForCountAsync(List<string> sink, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (sink)
            {
                if (sink.Count >= count)
                { return; }
            }
            await Task.Delay(20);
        }
    }

}