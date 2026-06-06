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
            case ReplyContentType.ToolCall:
                return "ok";

            case ReplyContentType.Error:
                await SpeakAsync(session, $"Hubo un error: {p.Content}", p.ConversationId, tts, settings, metrics, default);
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
        IMetricsPublisher metrics)
    {
        var text = accumulator.Flush(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        await SpeakAsync(session, text, conversationId, tts, settings, metrics, default);
        return true;
    }

    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        CancellationToken ct)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = new PlaybackJob(
            Label: $"reply:{session.SatelliteId}",
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, ct),
            OnStarted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.WakeToFirstAudioMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = sw.ElapsedMilliseconds,
                    ConversationId = conversationId
                }, ct);
            },
            OnPreempted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePreemptedReply,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    ConversationId = conversationId
                }, ct);
            },
            OnDrained: () => { session.SignalTurnSpoken(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);

        await metrics.PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.TtsLatencyMs,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            DurationMs = sw.ElapsedMilliseconds,
            ConversationId = conversationId
        }, ct);
    }
}