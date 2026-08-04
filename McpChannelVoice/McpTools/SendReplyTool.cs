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
            return HandleUtteranceReply(session, p, accumulator, tts, settings, metrics, time, logger);
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

    private static string HandleUtteranceReply(
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
                if (session.Turn.TryClaimPreamble())
                {
                    _ = FlushAndSpeak(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger, isReply: false);
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
                    _ = FlushAndSpeak(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                }
                return "ok";

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                // Speak whatever tail is left after the streamed segments, then tell the turn the
                // agent has stopped sending. Whether that settles the turn silent or leaves it
                // waiting on audio still playing is the turn's decision: streaming may already have
                // spoken everything, leaving this flush empty.
                _ = FlushAndSpeak(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                session.Turn.EndStream();
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (p.IsComplete)
                {
                    _ = FlushAndSpeak(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
                    return "ok";
                }
                SpeakReadySegments(session, accumulator, p.ConversationId, tts, settings, metrics, time, logger);
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

    private static bool FlushAndSpeak(
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
        Speak(session, text, conversationId, tts, settings, metrics, time, logger, isReply, default);
        return true;
    }

    // Drains every complete sentence run the buffer now holds into the playback queue, so the user
    // hears the answer's opening while the agent is still generating its end. The first run clears a
    // deliberately low bar (it is the wait everyone feels); later ones need more text, because each
    // is its own TTS request and the audio already playing is covering them.
    private static void SpeakReadySegments(
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
            // Checked BEFORE taking the text, not after: TryTakeSpeakable removes the run from the
            // buffer, so a refused enqueue used to discard it outright — the user heard an answer
            // with a hole in the middle while the turn still settled Spoken. Leaving it buffered
            // means the next chunk, or the StreamComplete flush, still speaks it.
            if (!session.Playback.CanAccept(PlaybackKind.Reply))
            {
                return;
            }

            var minChars = session.Turn.NextSegmentIsFirst
                ? streaming.FirstSegmentMinChars
                : streaming.MinChars;
            if (!accumulator.TryTakeSpeakable(conversationId, minChars, out var segment))
            {
                return;
            }

            Speak(
                session, segment, conversationId, tts, settings, metrics, time, logger,
                isReply: true, default);
        }
    }

    // Synchronous, because queueing is: the segment's synthesis is handed to the queue rather than
    // started here, and the queue answers immediately.
    private static void Speak(
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
        var options = new SynthesisOptions { Voice = session.ResolveVoice(settings) };

        // Assigned by BeginSegment below, before the enqueue and therefore before any callback can
        // run. The token carries the epoch it was registered under, so the count and its release
        // cannot land on different turns. A preamble job never registers one and never releases it.
        var segment = default(SegmentToken);

        // Reply text arriving here closes the hub-visible agent round trip: dispatch -> answer.
        // Compared against the agent's own MemoryRecall + LlmTotal, the difference is queue time.
        // TryConsumeDispatchedAt is single-use and consumed here regardless of whether the publish
        // below succeeds, so a metrics blip can never leave the stamp behind for some later, unrelated
        // reply on this session (e.g. a schedule firing into a satellite that had a real turn earlier)
        // to pick up and report as its own invented round trip. The consumed value doubles as the
        // proof that this reply answers a transcript this hub dispatched, which the turn-anchored
        // spans below need — see the OnFirstAudio gate.
        var dispatchedAtStamp = isReply ? session.Turn.TryConsumeDispatchedAt() : null;

        // The end bound of the agent round trip and the start of the queue wait, so the two spans
        // meet exactly.
        var enqueuedAt = time.GetTimestamp();

        if (dispatchedAtStamp is { } dispatchedAt)
        {
            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.AgentRoundTripMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = (long)time.GetElapsedTime(dispatchedAt, enqueuedAt).TotalMilliseconds,
                ConversationId = conversationId
            });
        }

        // Latency is still measured in the loop, and still means the same thing: for the first reply
        // segment the loop pulls immediately, so it observes the real synthesis time. Later segments
        // find their audio already buffered — but they do not publish TtsLatencyMs, so the
        // decomposition is unaffected.
        var job = new PlaybackJob(
            Label: $"{(isReply ? "reply" : "preamble")}:{session.SatelliteId}",
            Kind: isReply ? PlaybackKind.Reply : PlaybackKind.Preamble,
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, ct),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            EnqueuedAt: enqueuedAt,
            // Anchored to the turn's FIRST reply segment — never the preamble flush, which runs
            // with isReply: false and publishes nothing. Under streaming that segment is the first
            // flushable sentence the model produced: normally the answer's opening, but a model
            // that narrates a complete sentence before its first tool call anchors here too. That
            // is deliberate — these spans measure how long the user waited to hear a reply, and
            // narration the user hears IS the reply starting; anchoring at the later answer would
            // also leave the queue-wait and TTS spans measured against a different job than the
            // turn spans, and the decomposition would stop summing.
            OnFirstAudio: timing =>
            {
                if (!segment.IsFirst)
                {
                    return Task.CompletedTask;
                }

                metrics.Publish(new VoiceEvent
                {
                    Metric = VoiceMetric.TtsLatencyMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = (long)timing.SinceSynthesisStart.TotalMilliseconds,
                    ConversationId = conversationId
                });

                // Anchored on this job alone (its own enqueue stamp), so it is valid for every reply
                // regardless of what preceded it.
                if (timing.QueueWait is { } queueWait)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.ReplyQueueWaitMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)queueWait.TotalMilliseconds,
                        ConversationId = conversationId
                    });
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
                    return Task.CompletedTask;
                }

                if (timing.SinceTurnStart is { } turn)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeToFirstAudioMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)turn.TotalMilliseconds,
                        ConversationId = conversationId
                    });
                }

                if (timing.SinceSpeechEnd is { } sinceSpeech)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.SpeechEndToFirstAudioMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)sinceSpeech.TotalMilliseconds,
                        ConversationId = conversationId
                    });
                }

                return Task.CompletedTask;
            });

        // Counted before the enqueue so the job cannot drain against a segment that was never
        // registered. A refused enqueue (queue full, or the satellite disconnected and completed the
        // writer) settles the segment immediately — otherwise the handshake would wait out the
        // ~120s ReplyTimeoutMs for audio that will never play.
        if (isReply)
        {
            segment = session.Turn.BeginSegment();
        }

        var ticket = session.Playback.Enqueue(job);
        if (isReply && ticket.Refused is { } reason)
        {
            logger.LogWarning(
                "Reply segment for {Satellite} was refused by the playback queue ({Reason}); " +
                "this part of the answer will not be spoken", session.SatelliteId, reason);
        }

        if (!isReply)
        {
            return;
        }

        // One binding for every way the job can end, where there used to be a release on three
        // separate paths and a disposal on the same three. A missed release left the turn
        // outstanding until the ~120 s reply timeout, with the microphone shut for all of it.
        _ = ticket.Completed.ContinueWith(
            settled => SettleSegment(settled.Result, segment, session, conversationId, metrics, logger),
            TaskScheduler.Default);
    }

    // Runs unobserved on the queue's signal, so it guards itself: the queue no longer swallows what
    // a producer throws, and a segment left outstanding is exactly the wedged microphone above.
    private static void SettleSegment(
        PlaybackOutcome outcome,
        SegmentToken segment,
        SatelliteSession session,
        string conversationId,
        IMetricsPublisher metrics,
        ILogger<SendReplyTool> logger)
    {
        try
        {
            // A drain resolves this SEGMENT, not the turn: the turn settles once every started
            // segment has drained and the agent's stream has ended. Every other ending fails the
            // segment — the turn still settles Spoken when earlier audio reached the satellite,
            // which is what an alarm cutting into a reply looks like, and Silent when none did.
            if (outcome.Kind == PlaybackOutcomeKind.Drained)
            {
                segment.Complete();
                return;
            }

            segment.Fail();

            // One sample per turn, like the other reply-anchored metrics.
            if (outcome.Kind != PlaybackOutcomeKind.Preempted || !segment.IsFirst)
            {
                return;
            }

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.AnnouncePreemptedReply,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                ConversationId = conversationId
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Settling the reply segment for {Satellite} failed", session.SatelliteId);
        }
    }
}