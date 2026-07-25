using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var p = new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId
        };

        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();
        var logger = services.GetRequiredService<ILogger<SendReplyTool>>();

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is not null)
        {
            // Only the live-session (utterance reply) path stamps enqueue timing; the scheduled
            // delivery path below goes through AnnouncementService instead, so TimeProvider is
            // resolved here rather than unconditionally at the top of McpRun.
            var time = services.GetRequiredService<TimeProvider>();
            return await HandleUtteranceReplyAsync(session, p, accumulator, tts, settings, metrics, time, logger);
        }

        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var target = delivery.Resolve(p.ConversationId);
        if (target is not null)
        {
            var announcer = services.GetRequiredService<AnnouncementService>();
            return await HandleScheduledDeliveryAsync(p, target, delivery, accumulator, announcer, logger);
        }

        return "ok";
    }

    private static async Task<string> HandleUtteranceReplyAsync(
        SatelliteSession session,
        SendReplyParams p,
        ReplyTextAccumulator accumulator,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        TimeProvider time,
        ILogger<SendReplyTool> logger)
    {
        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
                return "ok";

            // The agent is told to say one word ("Buscando") before slow multi-tool work so the user
            // hears that something started. Text chunks are buffered until StreamComplete, so without
            // this flush that word is spoken glued to the front of the answer — after the wait it
            // exists to cover, costing words and buying nothing. The first tool call of the turn is
            // the moment the wait becomes real, so speak it here. It is a cue, not the reply: it must
            // not resolve the turn handshake (that would end FollowUpConversation and re-arm the mic
            // mid-turn) and it must not publish the reply-latency metrics, which measure time-to-answer.
            case ReplyContentType.ToolCall:
                if (session.TryClaimPreamble())
                {
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger, isReply: false);
                }
                return "ok";

            case ReplyContentType.Error:
                // Treat the error as terminal reply text: append it so any buffered partial answer
                // and the error are spoken together, in order, by the trailing StreamComplete —
                // not the error first with the leftover partial spoken after it. Mirrors the
                // flush-on-error contract honored by the Telegram/ServiceBus channels and voice's
                // own scheduled path. (ChatMonitor sends Error with isComplete=false then a
                // StreamComplete; the isComplete guard only covers a transport that completes early.)
                accumulator.Append(p.ConversationId, $" Hubo un error: {p.Content}");
                if (p.IsComplete)
                {
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                }
                return "ok";

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                // Speak whatever tail is left after the streamed segments, then close the handshake.
                // ReplySegmentsStarted is the authority on whether the turn produced audio at all:
                // streaming may already have spoken everything, leaving this flush empty.
                _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                if (session.ReplySegmentsStarted == 0)
                {
                    session.SignalTurnSilent();
                }
                else
                {
                    session.MarkReplyStreamComplete();
                }
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (p.IsComplete)
                {
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                    return "ok";
                }
                await SpeakReadySegmentsAsync(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                return "ok";
        }
    }

    private static async Task<string> HandleScheduledDeliveryAsync(
        SendReplyParams p,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        ReplyTextAccumulator accumulator,
        AnnouncementService announcer,
        ILogger<SendReplyTool> logger)
    {
        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
                return "ok";

            // An unsolicited scheduled delivery prefers silence over announcing a failure
            // (e.g. at night). Drop the buffer and the binding without speaking.
            case ReplyContentType.Error:
                accumulator.Flush(p.ConversationId);
                delivery.Remove(p.ConversationId);
                logger.LogWarning("Scheduled voice delivery {ConversationId} errored; not speaking", p.ConversationId);
                return "ok";

            case ReplyContentType.StreamComplete:
                await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, accumulator, announcer, logger);
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                if (p.IsComplete)
                {
                    await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, accumulator, announcer, logger);
                }
                return "ok";
        }
    }

    private static async Task AnnounceAccumulatedAsync(
        string conversationId,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        ReplyTextAccumulator accumulator,
        AnnouncementService announcer,
        ILogger<SendReplyTool> logger)
    {
        var text = accumulator.Flush(conversationId);
        delivery.Remove(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await announcer.AnnounceAsync(new AnnounceRequest { Target = target, Text = text }, default);
        }
        catch (AnnounceTargetNotFoundException ex)
        {
            logger.LogWarning(ex, "Scheduled voice delivery {ConversationId} had no matching satellites", conversationId);
        }
    }

    private static async Task<bool> FlushAndSpeakAsync(
        SatelliteSession session,
        ReplyTextAccumulator accumulator,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        TimeProvider time,
        ILogger<SendReplyTool> logger,
        bool isReply = true)
    {
        var text = accumulator.Flush(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        await SpeakAsync(session, text, conversationId, tts, settings, metrics, time, logger, isReply, default);
        return true;
    }

    // Drains every complete sentence run the buffer now holds into the playback queue, so the user
    // hears the answer's opening while the agent is still generating its end. The first run clears a
    // deliberately low bar (it is the wait everyone feels); later ones need more text, because each
    // is its own TTS request and the audio already playing is covering them.
    private static async Task SpeakReadySegmentsAsync(
        SatelliteSession session,
        ReplyTextAccumulator accumulator,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        TimeProvider time,
        ILogger<SendReplyTool> logger)
    {
        var streaming = settings.Tts.Streaming;
        if (!streaming.Enabled)
        {
            return;
        }

        while (true)
        {
            var minChars = session.ReplySegmentsStarted == 0
                ? streaming.FirstSegmentMinChars
                : streaming.MinChars;
            if (!accumulator.TryTakeSpeakable(conversationId, minChars, out var segment))
            {
                return;
            }

            await SpeakAsync(
                session, segment, conversationId, tts, settings, metrics, time, logger,
                isReply: true, default);
        }
    }

    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        TimeProvider time,
        ILogger<SendReplyTool> logger,
        bool isReply,
        CancellationToken ct)
    {
        var voice = session.Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice;
        var options = new SynthesisOptions { Voice = voice };

        // Reply text arriving here closes the hub-visible agent round trip: dispatch -> answer.
        // Compared against the agent's own MemoryRecall + LlmTotal, the difference is queue time.
        // TryConsumeDispatchedAt is single-use and consumed here regardless of whether the publish
        // below succeeds, so a metrics blip can never leave the stamp behind for some later, unrelated
        // reply on this session (e.g. a schedule firing into a satellite that had a real turn earlier)
        // to pick up and report as its own invented round trip. The consumed value doubles as the
        // proof that this reply answers a transcript this hub dispatched, which the turn-anchored
        // spans below need — see the OnFirstAudio gate.
        var dispatchedAtStamp = isReply ? session.TryConsumeDispatchedAt() : null;

        // Taken before the publish below and used as its end bound, so the round trip ends exactly
        // where the queue wait begins: the publish is itself an awaited Redis round trip, and stamping
        // afterwards left its cost in no span at all on every turn.
        var enqueuedAt = time.GetTimestamp();

        if (dispatchedAtStamp is { } dispatchedAt)
        {
            try
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AgentRoundTripMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = (long)time.GetElapsedTime(dispatchedAt, enqueuedAt).TotalMilliseconds,
                    ConversationId = conversationId
                }, ct);
            }
            catch (Exception ex)
            {
                // A metrics-publisher blip must not fault this call before the reply job below is
                // even built — that would leave the user's answer unspoken and the turn handshake
                // unsettled until the ~120s ReplyTimeoutMs.
                logger.LogWarning(ex, "Failed to publish AgentRoundTripMs metric");
            }
        }

        // Synthesis is lazy and runs inside the playback loop, so latency must be measured there.
        // The loop times synthesis -> first audio chunk (TtsLatencyMs) and turn-open -> first audio
        // (WakeToFirstAudioMs); emitting from here would only ever record the ~0 ms enqueue.
        var job = new PlaybackJob(
            Label: $"{(isReply ? "reply" : "preamble")}:{session.SatelliteId}",
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, ct),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: async _ =>
            {
                if (!isReply)
                {
                    return;
                }
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePreemptedReply,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    ConversationId = conversationId
                }, ct);
            },
            // The reply is several sentence jobs now, so a drain resolves this SEGMENT; the turn only
            // settles once every started segment has drained and the agent's stream has ended.
            // Signalling here directly would end the turn on sentence one and reopen the mic over the
            // rest of the answer.
            OnDrained: () => { if (isReply) { session.CompleteReplySegment(); } return Task.CompletedTask; },
            // If synthesis/playback fails (e.g. a Wyoming TTS error event throws), resolve the turn
            // as silent so FollowUpConversation ends and re-arms wake instead of blocking on the
            // handshake until the ~120s ReplyTimeoutMs. No audio actually played, hence Silent (not
            // Spoken). Mirrors the chime and approval jobs, which also settle their handshake on failure.
            // A failed preamble settles nothing: the answer still owes the handshake a signal.
            OnFailed: _ => { if (isReply) { session.FailReplySegment(); } return Task.CompletedTask; },
            EnqueuedAt: enqueuedAt,
            // Reply-latency metrics stay anchored to the ANSWER, not the preamble cue, so
            // WakeToFirstAudioMs keeps meaning the same thing before and after this change.
            OnFirstAudio: async timing =>
            {
                if (!isReply)
                {
                    return;
                }

                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.TtsLatencyMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = (long)timing.SinceSynthesisStart.TotalMilliseconds,
                    ConversationId = conversationId
                }, ct);

                // Anchored on this job alone (its own enqueue stamp), so it is valid for every reply
                // regardless of what preceded it.
                if (timing.QueueWait is { } queueWait)
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.ReplyQueueWaitMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)queueWait.TotalMilliseconds,
                        ConversationId = conversationId
                    }, ct);
                }

                // The two below are anchored on the TURN (MarkTurnStart / MarkSpeechEnd), which is
                // never invalidated, while the voice conversation mapping outlives the turn by
                // ConversationLifetime. A schedule fire or an agent-initiated message delivered into a
                // live session comes down this same path, so without a gate it reports the AGE of the
                // last real turn as its own latency — one "recuérdame en dos minutos" publishes
                // ~120000 ms and wrecks Avg/P95/Max on the headline metric. The consumed dispatch
                // stamp is the proof that what is being answered is a transcript this hub dispatched.
                if (dispatchedAtStamp is null)
                {
                    return;
                }

                if (timing.SinceTurnStart is { } turn)
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeToFirstAudioMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)turn.TotalMilliseconds,
                        ConversationId = conversationId
                    }, ct);
                }

                if (timing.SinceSpeechEnd is { } sinceSpeech)
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.SpeechEndToFirstAudioMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)sinceSpeech.TotalMilliseconds,
                        ConversationId = conversationId
                    }, ct);
                }
            });

        // Counted before the enqueue so the job cannot drain against a segment that was never
        // registered. A refused enqueue (queue full, or the satellite disconnected and completed the
        // writer) settles the segment immediately — otherwise the handshake would wait out the
        // ~120s ReplyTimeoutMs for audio that will never play.
        if (isReply)
        {
            session.BeginReplySegment();
        }

        var queued = await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
        if (isReply && !queued)
        {
            logger.LogWarning(
                "Reply segment for {Satellite} was refused by the playback queue (depth {Depth}); " +
                "the answer will be truncated", session.SatelliteId, settings.Announce.QueueMaxDepth);
            session.FailReplySegment();
        }
    }
}