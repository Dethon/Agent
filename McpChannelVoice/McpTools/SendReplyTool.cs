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

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is not null)
        {
            return await HandleUtteranceReplyAsync(session, p, accumulator, tts, settings, metrics);
        }

        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var target = delivery.Resolve(p.ConversationId);
        if (target is not null)
        {
            var announcer = services.GetRequiredService<AnnouncementService>();
            var logger = services.GetRequiredService<ILogger<SendReplyTool>>();
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
        IMetricsPublisher metrics)
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
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics, isReply: false);
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
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                }
                return "ok";

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                var spoke = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                if (!spoke)
                {
                    session.SignalTurnSilent();
                }
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (p.IsComplete)
                {
                    _ = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                }
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
        bool isReply = true)
    {
        var text = accumulator.Flush(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        await SpeakAsync(session, text, conversationId, tts, settings, metrics, isReply, default);
        return true;
    }

    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        bool isReply,
        CancellationToken ct)
    {
        var voice = session.Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice;
        var options = new SynthesisOptions { Voice = voice };

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
            OnDrained: () => { if (isReply) { session.SignalTurnSpoken(); } return Task.CompletedTask; },
            // If synthesis/playback fails (e.g. a Wyoming TTS error event throws), resolve the turn
            // as silent so FollowUpConversation ends and re-arms wake instead of blocking on the
            // handshake until the ~120s ReplyTimeoutMs. No audio actually played, hence Silent (not
            // Spoken). Mirrors the chime and approval jobs, which also settle their handshake on failure.
            // A failed preamble settles nothing: the answer still owes the handshake a signal.
            OnFailed: _ => { if (isReply) { session.SignalTurnSilent(); } return Task.CompletedTask; },
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
            });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
    }
}